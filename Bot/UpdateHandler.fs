module Telebot.UpdateHandler

open System
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Serilog
open Telebot.Blacklist
open Telebot.Bus
open Telebot.Messages
open Telebot.PrometheusMetrics
open Telebot.Replies
open Telebot.TelemetryService

let private getChatOrUserName (chat: Types.Chat) (user: Types.User option) =
    match chat.Title with
    | Some title when not (System.String.IsNullOrWhiteSpace(title)) -> title
    | _ ->
        match user with
        | Some u ->
            match u.Username with
            | Some username when not (System.String.IsNullOrWhiteSpace(username)) -> $"@{username}"
            | _ ->
                let first = u.FirstName
                let last = defaultArg u.LastName ""
                let full = $"{first} {last}".Trim()
                if System.String.IsNullOrWhiteSpace(full) then $"User_{u.Id}" else full
        | None ->
            match chat.Username with
            | Some username when not (System.String.IsNullOrWhiteSpace(username)) -> $"@{username}"
            | _ ->
                let first = defaultArg chat.FirstName ""
                let last = defaultArg chat.LastName ""
                let full = $"{first} {last}".Trim()
                if System.String.IsNullOrWhiteSpace(full) then $"Chat_{chat.Id}" else full

let private getChatType (chat: Types.Chat) =
    match chat.Type with
    | ChatType.Private -> "private"
    | _ -> "group"

let private getUserName (user: Types.User option) =
    match user with
    | Some u ->
        match u.Username with
        | Some username when not (System.String.IsNullOrWhiteSpace(username)) -> $"@{username}"
        | _ ->
            let first = u.FirstName
            let last = defaultArg u.LastName ""
            let full = $"{first} {last}".Trim()
            if System.String.IsNullOrWhiteSpace(full) then $"User_{u.Id}" else full
    | None -> "Unknown"

let private stripHtml (html: string) : string =
    if System.String.IsNullOrWhiteSpace(html) then ""
    else
        html
            .Replace("<br>", "\n")
            .Replace("<br/>", "\n")
            .Replace("<br />", "\n")
            .Replace("<blockquote>", "\n“")
            .Replace("</blockquote>", "”\n")
            .Replace("<b>", "")
            .Replace("</b>", "")
            .Replace("<i>", "")
            .Replace("</i>", "")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&amp;", "&")
            .Trim()

let private hasSupportedLinks (text: string option) =
    let ig = getLinks Instagram.Instagram.postRegex text @ getLinks Instagram.Instagram.shareRegex text
    let tw = getLinks Twitter.Twitter.twitterRegex text
    let tt = getLinks TikTok.TikTok.tikTokRegex text
    let yt = getLinks Youtube.Youtube.youtubeRegex text
    not (List.isEmpty ig && List.isEmpty tw && List.isEmpty tt && List.isEmpty yt)

let private answerCallbackQueryAsync (ctx: UpdateContext) (cb: CallbackQuery) text showAlert =
    Req.AnswerCallbackQuery.Make(cb.Id, ?text = text, ?showAlert = showAlert)
    |> api ctx.Config
    |> Async.Ignore

// Callback query handling for translation toggle buttons and popups
let private handleCallbackQueryAsync (ctx: UpdateContext) (cb: CallbackQuery) : Async<unit> =
    async {
        try
            match cb.Data with
            | Some data when data.StartsWith("pop_orig:") ->
                let cacheId = data.Substring("pop_orig:".Length)
                match Translation.tryGetTranslationFromCache cacheId with
                | Some cached ->
                    let plainText = stripHtml cached.OriginalText
                    let popupText: string = if plainText.Length > 200 then plainText.Substring(0, 197) + "..." else plainText
                    do! answerCallbackQueryAsync ctx cb (Some popupText) (Some true)
                | None ->
                    do! answerCallbackQueryAsync ctx cb (Some "Original text not found or expired.") (Some true)

            | Some data when data.StartsWith("show_orig:") || data.StartsWith("show_trans:") ->
                let isOrig = data.StartsWith("show_orig:")
                let cacheId = if isOrig then data.Substring("show_orig:".Length) else data.Substring("show_trans:".Length)
                match Translation.tryGetTranslationFromCache cacheId with
                | Some cached ->
                    let newText = if isOrig then cached.OriginalText else cached.TranslatedText
                    let newToggleLabel = if isOrig then "Show Translated Text" else "Show Original Text"
                    let newToggleData = if isOrig then $"show_trans:{cacheId}" else $"show_orig:{cacheId}"

                    let btnToggle = InlineKeyboardButton.Create(newToggleLabel, callbackData = newToggleData)
                    let isGroupChat =
                        match cb.Message with
                        | Some (MaybeInaccessibleMessage.Message msg) -> msg.Chat.Id < 0L
                        | _ -> false

                    let buttons =
                        if not isGroupChat then
                            match Config.get().WebAppBaseUrl with
                            | Some webAppBase ->
                                let url = $"{webAppBase.TrimEnd('/')}/webapp?id={cacheId}"
                                let btnWebApp = InlineKeyboardButton.Create("Original (Web)", webApp = WebAppInfo.Create(url))
                                [| [| btnWebApp; btnToggle |] |]
                            | None ->
                                [| [| btnToggle |] |]
                        else
                            [| [| btnToggle |] |]

                    let keyboard = InlineKeyboardMarkup.Create(buttons)

                    do! answerCallbackQueryAsync ctx cb None None

                    match cb.Message with
                    | Some (MaybeInaccessibleMessage.Message msg) ->
                        let chatId = ChatId.Int msg.Chat.Id
                        if msg.Text.IsSome then
                            let editReq =
                                Req.EditMessageText.Make(
                                    chatId = chatId,
                                    messageId = msg.MessageId,
                                    text = newText,
                                    parseMode = ParseMode.HTML,
                                    replyMarkup = keyboard
                                )
                            do! editReq |> api ctx.Config |> Async.Ignore
                        else
                            let editReq =
                                Req.EditMessageCaption.Make(
                                    chatId = chatId,
                                    messageId = msg.MessageId,
                                    caption = newText,
                                    parseMode = ParseMode.HTML,
                                    replyMarkup = keyboard
                                )
                            do! editReq |> api ctx.Config |> Async.Ignore
                    | _ -> ()
                | None ->
                    do! answerCallbackQueryAsync ctx cb (Some "Translation not found or expired.") (Some true)
            | _ ->
                do! answerCallbackQueryAsync ctx cb None None
        with ex ->
            Log.Error(ex, "Error processing callback query")
    }

// Enhanced update handler with telemetry
let updateArrivedAsync (ctx: UpdateContext) : Async<unit> =
    async {
        match ctx.Update.Message, ctx.Update.CallbackQuery with
        | Some {
                   MessageId = messageId
                   Chat = chat
                   Text = messageText
                   From = user
               }, _ ->
            let chatName = getChatOrUserName chat user
            let chatType = getChatType chat
            let senderName = getUserName user
            receivedMessagesCounter.WithLabels([|chatName; chatType; senderName|]).Inc()

            // Check blacklist
            let userId = user |> Option.map _.Id
            if isBlacklisted chat.Id userId then
               Log.Information($"Message ignored due to blacklist. ChatId: {chat.Id}, UserId: {userId}")
               unprocessedMessagesCounter.WithLabels([|chatName; chatType; senderName|]).Inc()
            else
                // Create telemetry context
                let chatIdStr = chat.Id.ToString()
                let messageIdStr = messageId.ToString()
                let userIdStr = user |> Option.map _.Id.ToString()

                return! withMessageTelemetry chatIdStr messageIdStr "process_telegram_message" (fun scope ->
                    async {
                        // Add user information to telemetry
                        userIdStr |> Option.iter (fun id -> TelemetryScope.addProperty "user_id" id scope |> ignore)
                        TelemetryScope.addProperty "chat_type" (chat.Type.ToString()) scope |> ignore
                        messageText |> Option.iter (fun text ->
                            TelemetryScope.addProperty "message_length" text.Length scope |> ignore
                            TelemetryScope.addProperty "has_text" true scope |> ignore
                        )

                        TelemetryScope.logInfo $"Processing message {messageId} from chat {chat.Id}" scope

                        // Increment metrics
                        newMessageCounter.Inc()

                        if not (hasSupportedLinks messageText) then
                            unprocessedMessagesCounter.WithLabels([|chatName; chatType; senderName|]).Inc()

                        // Create message data
                        let mId = MessageId.Create messageId
                        let cId = ChatId.Int chat.Id

                        let updateMessage = {
                            MessageText = messageText
                            MessageId = mId
                            ChatId = cId
                        }

                        // Send to bus asynchronously
                        do! sendToBusAsync updateMessage

                        TelemetryScope.logInfo "Message processed and sent to bus successfully" scope
                    }
                )
        | _, Some cb ->
            return! handleCallbackQueryAsync ctx cb
        | _ ->
            return ()
    }
