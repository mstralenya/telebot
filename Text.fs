module Telebot.Text

open System.Text.RegularExpressions
open System.IO
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Serilog
open Telebot.DataTypes
open Telebot.VideoDownloader

module Reply =
    let createVideoFileWithCaption file caption =
        VideoFile { File = file; Caption = caption }

    let createVideoFile file = createVideoFileWithCaption file None

    let createGallery files caption =
        Gallery
            {
                Media = List.ofArray files
                Caption = caption
            }

    let createAudioFile audio = AudioFile audio

    /// Creates a Message reply.
    let createMessage text = Message text

let getLinks (regex: Regex) (text: string option) =
    text
    |> Option.map (fun text -> regex.Matches text |> Seq.cast<Match> |> Seq.map _.Value |> Seq.toList)
    |> Option.defaultValue List.empty

let truncateWithEllipsis (input: string option) (maxLength: int) : string option =
    match input with
    | Some str ->
        if str.Length <= maxLength then
            Some str
        else
            let truncated = str.Substring(0, maxLength - 3)
            Some(truncated + "...")
    | None -> None


let sendRequestAsync (req: 'TReq) (ctx: UpdateContext) = req |> api ctx.Config |> Async.Ignore


let private sendMediaWithCaption
    (fileToSend: string)
    (caption: string option)
    (messageId: MessageId)
    (chatId: ChatId)
    (ctx: UpdateContext)
    =
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

    let videoFile = InputFile.File(videoPath, File.OpenRead videoPath)
    videoFile, thumbnailFilename, duration, width, height

let private sendVideoWithThumbnail
    (videoPath: string)
    (caption: string option)
    (messageId: MessageId)
    (chatId: ChatId)
    (ctx: UpdateContext)
    =
    let videoFile, thumbFile, duration, width, height = getVideoInputFile videoPath
     
    let file = if thumbFile.IsSome then Some(InputFile.File(thumbFile.Value, File.OpenRead thumbFile.Value)) else None

    let req =
        Req.SendVideo.Make(
            chatId,
            videoFile,
            parseMode = ParseMode.HTML,
            replyParameters = ReplyParameters.Create(messageId.MessageId, chatId),
            showCaptionAboveMedia = true,
            disableNotification = true,
            supportsStreaming = true,
            ?thumbnail = file,
            ?width = width,
            ?height = height,
            ?duration = duration,
            ?caption = caption
        )

    sendRequestAsync req ctx |> Async.RunSynchronously
    [ getThumbnailName videoPath; videoPath ] |> Seq.iter deleteFile

let private sendAudio (audioPath: string) (messageId: MessageId) (chatId: ChatId) (ctx: UpdateContext) =
    let req =
        Req.SendAudio.Make(
            chatId,
            InputFile.File(audioPath, File.OpenRead audioPath),
            replyParameters = ReplyParameters.Create(messageId.MessageId, chatId),
            disableNotification = true
        )

    sendRequestAsync req ctx |> Async.RunSynchronously
    [ audioPath ] |> Seq.iter deleteFile

let private createMediaInput (media: GalleryDisplay) =
    match media with
    | Photo p -> InputMedia.Photo(InputMediaPhoto.Create("photo", InputFile.File(p, File.OpenRead p), parseMode = ParseMode.HTML))
    | Video v ->
        let videoFile, thumbFile, duration, width, height = getVideoInputFile v

        InputMedia.Video(
            InputMediaVideo.Create(
                "video",
                videoFile,
                parseMode = ParseMode.HTML,
                ?duration = duration,
                ?width = width,
                ?height = height,
                ?thumbnail = thumbFile
            )
        )

let private getMediaSize (media: GalleryDisplay) =
    let path =
        match media with
        | Photo p -> p
        | Video v -> v
    if File.Exists path then FileInfo(path).Length else 0L

let private sendMediaGallery
    (media: GalleryDisplay list)
    (caption: string option)
    (messageId: MessageId)
    (chatId: ChatId)
    (ctx: UpdateContext)
    =
    // Helper function to chunk media based on count (max 10) and total size (max 48MB)
    let chunkMedia (mediaList: GalleryDisplay list) =
        let maxCount = 10
        let maxSize = 48L * 1024L * 1024L // 48 MB

        let rec chunkRec currentChunk currentChunkSize currentCount remaining itemsAcc =
            match remaining with
            | [] ->
                if List.isEmpty currentChunk then itemsAcc
                else List.rev currentChunk :: itemsAcc
            | item :: tail ->
                let itemSize = getMediaSize item
                let newChunkSize = currentChunkSize + itemSize
                let newCount = currentCount + 1
                
                if (newChunkSize > maxSize || newCount > maxCount) && not (List.isEmpty currentChunk) then
                    chunkRec [item] itemSize 1 tail (List.rev currentChunk :: itemsAcc)
                else
                    chunkRec (item :: currentChunk) newChunkSize newCount tail itemsAcc

        chunkRec [] 0L 0 mediaList [] |> List.rev

    let chunks = chunkMedia media

    let textReq =
        caption
        |> Option.map (fun msg ->
            let req =
                Req.SendMessage.Make(
                    chatId,
                    msg,
                    replyParameters = ReplyParameters.Create(messageId.MessageId, chatId),
                    parseMode = ParseMode.HTML
                )

            sendRequestAsync req ctx)

    let mediaReqs =
        chunks
        |> Seq.map (fun chunk ->
            match chunk with
            | [] -> async { return () }
            | [ singleMedia ] ->
                // Single item chunks must be sent using the specific photo/video send methods
                // as sendMediaGroup requires at least 2 items.
                match singleMedia with
                | Photo p ->
                    let req =
                        Req.SendPhoto.Make(
                            chatId,
                            InputFile.File(p, File.OpenRead p),
                            parseMode = ParseMode.HTML,
                            replyParameters = ReplyParameters.Create(messageId.MessageId, chatId)
                        )
                    sendRequestAsync req ctx
                | Video v ->
                    let videoFile, thumbFile, duration, width, height = getVideoInputFile v
                    let file = if thumbFile.IsSome then Some(InputFile.File(thumbFile.Value, File.OpenRead thumbFile.Value)) else None
                    let req =
                        Req.SendVideo.Make(
                            chatId,
                            videoFile,
                            parseMode = ParseMode.HTML,
                            replyParameters = ReplyParameters.Create(messageId.MessageId, chatId),
                            showCaptionAboveMedia = true,
                            disableNotification = true,
                            supportsStreaming = true,
                            ?thumbnail = file,
                            ?width = width,
                            ?height = height,
                            ?duration = duration
                        )
                    sendRequestAsync req ctx
            | multipleMedia ->
                let g = multipleMedia |> List.map createMediaInput |> List.toArray
                let req =
                    Req.SendMediaGroup.Make(
                        chatId,
                        g,
                        disableNotification = true,
                        replyParameters = ReplyParameters.Create(messageId.MessageId, chatId)
                    )
                sendRequestAsync req ctx
        )

    match textReq with
    | Some textReq -> Seq.append (Seq.singleton textReq) mediaReqs
    | None -> mediaReqs
    |> Seq.iter Async.RunSynchronously

    // Correctly delete all temporary files and thumbnails
    media 
    |> Seq.collect (fun m ->
        match m with
        | Photo p -> seq [ p ]
        | Video v -> seq [ v; getThumbnailName v ]
    )
    |> Seq.iter deleteFile

let reply (reply: Reply, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) =
    match reply with
    | VideoFile videoFile ->
        let videoPath = shrinkVideoIfNeeded videoFile.File
        let fileInfo = FileInfo videoPath

        if fileInfo.Length > 50L * 1024L * 1024L then
            Log.Information $"File size is greater than 50 MB. Deleting file: %s{videoPath}"
            deleteFile videoPath
        else
            let caption = truncateWithEllipsis videoFile.Caption 1024
            sendVideoWithThumbnail videoPath caption messageId chatId ctx

    | AudioFile audioFile -> sendAudio audioFile messageId chatId ctx

    | Gallery imageGallery ->
        let caption = truncateWithEllipsis imageGallery.Caption 1024

        let processedMedia =
            imageGallery.Media
            |> List.map (function
                | Photo p -> Photo p
                | Video v -> Video (shrinkVideoIfNeeded v))

        match processedMedia with
        | [ singleMedia ] ->
            match singleMedia with
            | Photo p -> sendMediaWithCaption p caption messageId chatId ctx
            | Video v -> sendVideoWithThumbnail v caption messageId chatId ctx
        | media -> sendMediaGallery media caption messageId chatId ctx

    | Message message ->
        let req =
            Req.SendMessage.Make(
                chatId,
                message,
                replyParameters = ReplyParameters.Create(messageId.MessageId, chatId),
                parseMode = ParseMode.HTML
            )

        sendRequestAsync req ctx |> Async.Start
