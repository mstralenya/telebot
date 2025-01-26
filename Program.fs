module Program

open System
open System.IO
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Serilog
open Telebot.Thumbnail
open Telebot.Twitter
open Telebot.Youtube
open Telebot.Text
open Telebot.VideoDownloader
open Telebot.TikTok
open Telebot.Instagram

let sendRequestAsync (req: 'TReq) (ctx: UpdateContext) = req |> api ctx.Config |> Async.Ignore

let reply (reply: Reply, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) =
    match reply with
    | VideoFile videoFile ->
        // Check the file size
        let fileInfo = FileInfo(videoFile.File)

        if fileInfo.Length > 50L * 1024L * 1024L then
            Log.Information $"File size is greater than 50 MB. Deleting file: %s{videoFile.File}"
            deleteFile videoFile.File
        else
            let thumbnailFilename = $"{Guid.NewGuid()}.jpg"
            extractThumbnail videoFile.File thumbnailFilename
            let video = InputFile.File(videoFile.File, File.OpenRead(videoFile.File))
            let rt = videoFile.Caption
            let info = getVideoInfo videoFile.File

            let (duration, width, height) =
                match info with
                | Some(d, w, h) -> (Some d, Some w, Some h)
                | None -> (None, None, None)

            let thumbFile = InputFile.File(thumbnailFilename, File.OpenRead(thumbnailFilename))

            let req =
                Req.SendVideo.Make(
                    chatId,
                    video,
                    parseMode = ParseMode.HTML,
                    replyParameters = ReplyParameters.Create(messageId.MessageId, chatId),
                    showCaptionAboveMedia = true,
                    disableNotification = true,
                    supportsStreaming = true,
                    thumbnail = thumbFile,
                    ?width = width,
                    ?height = height,
                    ?duration = duration,
                    ?caption = rt
                )

            sendRequestAsync req ctx |> Async.RunSynchronously
            deleteFile videoFile.File
            deleteFile thumbnailFilename
    | ImageGallery imageGallery ->
        let gallery =
            imageGallery.Photos
            |> Seq.map (fun p ->
                InputMedia.Photo(
                    InputMediaPhoto.Create(
                        "photo",
                        InputFile.File(p, File.OpenRead(p)),
                        ?caption = imageGallery.Caption
                    )
                ))
            |> Seq.toArray

        let req =
            Req.SendMediaGroup.Make(
                chatId,
                gallery,
                disableNotification = true,
                replyParameters = ReplyParameters.Create(messageId.MessageId, chatId)
            )

        sendRequestAsync req ctx |> Async.RunSynchronously
        imageGallery.Photos |> Seq.iter deleteFile
    | Message message ->
        let req =
            Req.SendMessage.Make(chatId, message, replyParameters = ReplyParameters.Create(messageId.MessageId, chatId))

        sendRequestAsync req ctx |> Async.Start

let processVideos
    getLinks
    processVideo
    (messageText: string option, messageId: MessageId, chatId: ChatId, ctx: UpdateContext)
    =
    messageText
    |> getLinks messageText
    |> List.iter (fun link -> processVideo link |> Option.iter (fun r -> reply (r, messageId, chatId, ctx)))

let processLinks getLinks processVideo = processVideos getLinks processVideo

let processTikTokVideos = processLinks getTikTokLinks getTikTokReply
let processInstagramLinks = processLinks getInstagramLinks getInstagramReply
let processTwitterLinks = processLinks getTwitterLinks getTwitterReply
let processYoutubeLinks = processLinks getYoutubeLinks getYoutubeReply

let tryThreeTimes (processMessage: unit -> unit) =
    let rec tryWithRetries retriesLeft =
        try
            processMessage ()
        with ex ->
            if retriesLeft > 0 then
                Log.Error(ex, $"Error occured, retries left: {retriesLeft}")
                tryWithRetries (retriesLeft - 1)
            else
                Log.Error(ex, "Error occurred. No more retries left.")

    tryWithRetries 3

let updateArrived (ctx: UpdateContext) =
    match ctx.Update.Message with
    | Some { MessageId = messageId
             Chat = chat
             Text = messageText } ->
        let mId = MessageId.Create(messageId)
        let cId = ChatId.Int(chat.Id)

        [ processTikTokVideos
          processInstagramLinks
          processTwitterLinks
          processYoutubeLinks ]
        |> List.iter (fun processMessage -> tryThreeTimes (fun () -> processMessage (messageText, mId, cId, ctx)))
    | _ -> ()

type SerilogLogger() =
    interface Funogram.Types.IBotLogger with
        member _.Log(text) = Log.Information(text)
        member _.Enabled = true

[<EntryPoint>]
let main _ =
    Console.OutputEncoding <- Text.Encoding.UTF8
    Log.Logger <- LoggerConfiguration().WriteTo.Console().CreateLogger()

    async {
        let config =
            Config.defaultConfig |> Config.withReadTokenFromEnv "TELEGRAM_BOT_TOKEN"

        let config =
            { config with
                RequestLogger = Some(SerilogLogger()) }

        let! _ = Api.deleteWebhookBase () |> api config
        return! startBot config updateArrived None
    }
    |> Async.RunSynchronously

    Log.CloseAndFlush()
    0
