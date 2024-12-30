module Program

open System.IO;
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Telebot.Twitter
open dotenv.net
open Telebot.Text
open Telebot.VideoDownloader
open Telebot.TikTok
open Telebot.Instagram

DotEnv.Load()

let reply (reply: Reply, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) =
    match reply with
    | VideoFile (videoFile, replyText) ->
        let file = InputFile.File(videoFile, new FileStream(videoFile, FileMode.Open, FileAccess.Read))
        Req.SendVideo.Make(chatId=chatId, video=file, caption = replyText, parseMode = ParseMode.HTML, replyParameters=ReplyParameters.Create(messageId.MessageId, chatId))
        |> api ctx.Config
        |> Async.Ignore
        |> Async.RunSynchronously
        deleteVideo videoFile
    | Message message ->
        Req.SendMessage.Make(chatId, message, replyParameters=ReplyParameters.Create(messageId.MessageId, chatId))
        |> api ctx.Config
        |> Async.Ignore
        |> Async.Start

let processVideos getLinks processVideo (messageText: string option, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) =
    let links = getLinks messageText
    links |> List.iter (fun link ->
        let video = processVideo link
        match video with
        | Some (VideoFile (loadedVideoFile, replyText)) -> reply(VideoFile (loadedVideoFile, replyText), messageId, chatId, ctx)
        | Some (Message message) -> reply(Message message, messageId, chatId, ctx)
        | None -> ()
    )

let processTikTokVideos (messageText: string option, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) =
    processVideos getTikTokLinkIds getTikTokReply (messageText, messageId, chatId, ctx)

let processInstagramLinks (messageText: string option, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) =
    processVideos getInstagramLinks processInstagramVideo (messageText, messageId, chatId, ctx)
    
let processTwitterLinks (messageText: string option, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) =
    processVideos getTwitterLinks getTwitterReply (messageText, messageId, chatId, ctx)

let updateArrived (ctx: UpdateContext) =
  match ctx.Update.Message with
  | Some { MessageId = messageId; Chat = chat; Text = messageText } ->
    let mId = MessageId.Create(messageId)
    let cId = ChatId.Int(chat.Id)
    processTikTokVideos (messageText, mId, cId, ctx)
    processInstagramLinks (messageText, mId, cId, ctx)
    processTwitterLinks (messageText, mId, cId, ctx)
  | _ -> ()

[<EntryPoint>]
let main _ =
  async {
    let config = Config.defaultConfig |> Config.withReadTokenFromEnv "TELEGRAM_BOT_TOKEN"
    let! _ = Api.deleteWebhookBase () |> api config
    return! startBot config updateArrived None
  } |> Async.RunSynchronously
  0