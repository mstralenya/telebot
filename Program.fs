module Program

open System
open System.IO;
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Serilog
open Telebot.Twitter
open Telebot.Youtube
open dotenv.net
open Telebot.Text
open Telebot.VideoDownloader
open Telebot.TikTok
open Telebot.Instagram

DotEnv.Load()

let sendRequestAsync (req: 'TReq) (ctx: UpdateContext) =
    req |> api ctx.Config |> Async.Ignore

let reply (reply: Reply, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) =
    match reply with
    | VideoFile (videoFile, replyText, thumbnail) ->
        // Check the file size
        let fileInfo = FileInfo(videoFile)
        if fileInfo.Length > 50L * 1024L * 1024L then
            Log.Information $"File size is greater than 50 MB. Deleting file: %s{videoFile}"
            deleteFile videoFile
        else
            let file = InputFile.File(videoFile, File.OpenRead(videoFile))
            let rt = replyText |> Option.defaultValue ""
            let thumbnailInput = thumbnail |> Option.map (fun t -> InputFile.File(t, File.OpenRead(t)))
            let req =
                match thumbnailInput with
                | Some thumb ->
                    Req.SendVideo.Make(
                        chatId=chatId,
                        video=file,
                        caption=rt,
                        parseMode=ParseMode.HTML,
                        thumbnail=thumb,
                        replyParameters=ReplyParameters.Create(messageId.MessageId, chatId)
                    )
                | None ->
                    Req.SendVideo.Make(
                        chatId=chatId,
                        video=file,
                        caption=rt,
                        parseMode=ParseMode.HTML,
                        replyParameters=ReplyParameters.Create(messageId.MessageId, chatId)
                    )
            sendRequestAsync req ctx |> Async.RunSynchronously
            deleteFile videoFile
            thumbnail |> Option.map deleteFile |> Option.iter ignore
    | Message message ->
        let req = Req.SendMessage.Make(chatId, message, replyParameters=ReplyParameters.Create(messageId.MessageId, chatId))
        sendRequestAsync req ctx |> Async.Start

let processVideos getLinks processVideo (messageText: string option, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) =
    messageText
    |> getLinks messageText
    |> List.iter (fun link ->
        processVideo link
        |> Option.iter (fun r -> reply(r, messageId, chatId, ctx))
    )

let processLinks getLinks processVideo = processVideos getLinks processVideo

let processTikTokVideos = processLinks getTikTokLinks getTikTokReply
let processInstagramLinks = processLinks getInstagramLinks getInstagramReply
let processTwitterLinks = processLinks getTwitterLinks getTwitterReply
let processYoutubeLinks = processLinks getYoutubeLinks getYoutubeReply

let tryThreeTimes (processMessage: unit -> unit) =
    let rec tryWithRetries retriesLeft =
        try
            processMessage()
        with
        | ex ->
            if retriesLeft > 0 then
                tryWithRetries (retriesLeft - 1)
            else
                Log.Error(ex, "Error occurred. No more retries left.")
    tryWithRetries 3

let updateArrived (ctx: UpdateContext) =
    match ctx.Update.Message with
    | Some { MessageId = messageId; Chat = chat; Text = messageText } ->
        let mId = MessageId.Create(messageId)
        let cId = ChatId.Int(chat.Id)
        [
            processTikTokVideos
            processInstagramLinks
            processTwitterLinks
            processYoutubeLinks
        ]
        |> List.iter (fun processMessage -> tryThreeTimes (fun () -> processMessage(messageText, mId, cId, ctx)))
    | _ -> ()

[<EntryPoint>]
let main _ =
  Log.Logger <- LoggerConfiguration()
                    .WriteTo.Console()
                    .CreateLogger()
  async {
    let config = Config.defaultConfig |> Config.withReadTokenFromEnv "TELEGRAM_BOT_TOKEN"
    let! _ = Api.deleteWebhookBase () |> api config
    return! startBot config updateArrived None
  } |> Async.RunSynchronously
  Log.CloseAndFlush()
  0