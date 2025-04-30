module Telebot.Reply

open System.IO
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Serilog
open Telebot.Instagram
open Telebot.Text
open Telebot.Thumbnail
open Telebot.TikTok
open Telebot.Twitter
open Telebot.VideoDownloader
open Telebot.Youtube

let sendRequestAsync (req: 'TReq) (ctx: UpdateContext) = req |> api ctx.Config |> Async.Ignore

let private getVideoSize (filePath: string) =
    let info = getVideoInfo filePath
    let duration, width, height =
        match info with
        | Some(d, w, h) -> Some d, Some w, Some h
        | None -> None, None, None
    duration, width, height

let private sendMediaWithCaption (fileToSend: string) (caption: string option) (messageId: MessageId) (chatId: ChatId) (ctx: UpdateContext) =
    let req = 
        Req.SendPhoto.Make(
            chatId,
            InputFile.File(fileToSend, File.OpenRead fileToSend),
            parseMode = ParseMode.HTML,
            replyParameters = ReplyParameters.Create(messageId.MessageId, chatId),
            ?caption = caption
        )
    sendRequestAsync req ctx |> Async.RunSynchronously
    deleteFile fileToSend

let private getVideoInputFile (videoPath: string) =
    let thumbnailFilename = getVideoThumbnail videoPath
    let duration, width, height = getVideoSize videoPath

    let thumbFile = InputFile.File(thumbnailFilename, File.OpenRead thumbnailFilename)
    let videoFile = InputFile.File(videoPath, File.OpenRead videoPath)
    videoFile, thumbFile, duration, width, height

let private sendVideoWithThumbnail (videoPath: string) (caption: string option) (messageId: MessageId) (chatId: ChatId) (ctx: UpdateContext) =
    let videoFile, thumbFile, duration, width, height = getVideoInputFile videoPath    

    let req =
        Req.SendVideo.Make(
            chatId,
            videoFile,
            parseMode = ParseMode.HTML,
            replyParameters = ReplyParameters.Create(messageId.MessageId, chatId),
            showCaptionAboveMedia = true,
            disableNotification = true,
            supportsStreaming = true,
            thumbnail = thumbFile,
            ?width = width,
            ?height = height,
            ?duration = duration,
            ?caption = caption
        )
    sendRequestAsync req ctx |> Async.RunSynchronously
    [getThumbnailName videoPath; videoPath] |> Seq.iter deleteFile

let private createMediaInput (media: GalleryDisplay) =
    match media with
    | Photo p ->
        InputMedia.Photo(
            InputMediaPhoto.Create(
                "photo",
                InputFile.File(p, File.OpenRead p),
                ?parseMode = Some ParseMode.HTML
            ))
    | Video v ->
        let videoFile, thumbFile, duration, width, height = getVideoInputFile v
        InputMedia.Video(
            InputMediaVideo.Create(
                "video",
                videoFile,
                ?parseMode = Some ParseMode.HTML,
                ?duration = duration,
                ?width = width,
                ?height = height,
                thumbnail = thumbFile
            )
        )

let private sendMediaGallery (media: GalleryDisplay list) (caption: string option) (messageId: MessageId) (chatId: ChatId) (ctx: UpdateContext) =
    let gallery =
        media
        |> Seq.map createMediaInput
        |> Seq.chunkBySize 10
        |> Seq.toArray

    let textReq =
        caption 
        |> Option.map (fun msg ->
            let req = Req.SendMessage.Make(chatId, msg, replyParameters = ReplyParameters.Create(messageId.MessageId, chatId), parseMode = ParseMode.HTML)
            sendRequestAsync req ctx)

    let mediaReqs =
        gallery
        |> Seq.map (fun g ->
            Req.SendMediaGroup.Make(
                chatId,
                g,
                disableNotification = true,
                replyParameters = ReplyParameters.Create(messageId.MessageId, chatId)
            ))
        |> Seq.map (fun r -> sendRequestAsync r ctx)

    match textReq with
    | Some textReq -> Seq.append (Seq.singleton textReq) mediaReqs
    | None -> mediaReqs
    |> Seq.iter Async.RunSynchronously

    media |> Seq.map string |> Seq.iter deleteFile

let reply (reply: Reply, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) =
    match reply with
    | VideoFile videoFile ->
        let fileInfo = FileInfo videoFile.File
        if fileInfo.Length > 50L * 1024L * 1024L then
            Log.Information $"File size is greater than 50 MB. Deleting file: %s{videoFile.File}"
            deleteFile videoFile.File
        else
            let caption = truncateWithEllipsis videoFile.Caption 1024
            sendVideoWithThumbnail videoFile.File caption messageId chatId ctx

    | Gallery imageGallery ->
        let caption = truncateWithEllipsis imageGallery.Caption 1024
        match imageGallery.Media with
        | [singleMedia] ->
            match singleMedia with
            | Photo p -> sendMediaWithCaption p caption messageId chatId ctx
            | Video v -> sendVideoWithThumbnail v caption messageId chatId ctx
        | media -> sendMediaGallery media caption messageId chatId ctx

    | Message message ->
        let req = Req.SendMessage.Make(chatId, message, replyParameters = ReplyParameters.Create(messageId.MessageId, chatId), parseMode = ParseMode.HTML)
        sendRequestAsync req ctx |> Async.Start

let processVideos getLinks
    processVideo
    (messageText: string option, messageId: MessageId, chatId: ChatId, ctx: UpdateContext)
    =
    messageText
    |> getLinks messageText
    |> List.iter (fun link -> processVideo link |> Option.iter (fun r -> reply (r, messageId, chatId, ctx)))

let processLinks getLinks processVideo = processVideos getLinks processVideo

let processTikTokVideos = processLinks getTikTokLinks getTikTokReply
let processInstagramLinks = processLinks getInstagramLinks getInstagramReply
let processInstagramShareLinks = processLinks getInstagramShareLinks getInstagramShareReply
let processTwitterLinks = processLinks getTwitterLinks getTwitterReply
let processYoutubeLinks = processLinks getYoutubeLinks getYoutubeReply
