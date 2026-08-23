module Telebot.Replies

open System
open System.Text.RegularExpressions
open System.IO
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Serilog
open Telebot.DataTypes
open Telebot.Ffmpeg
open Telebot.VideoDownloader

module Reply =
    let createVideoFileWithCaption file caption =
        VideoFile { File = file; Caption = caption; ReplyMarkup = None }

    let createVideoFile file = createVideoFileWithCaption file None

    let createGallery files caption =
        Gallery
            {
                Media = List.ofArray files
                Caption = caption
                ReplyMarkup = None
            }

    let createAudioFile audio = AudioFile audio

    /// Creates a Message reply.
    let createMessage text = Message (text, None)

    let withMarkup (markup: Markup) (reply: Reply) =
        match reply with
        | VideoFile vf -> VideoFile { vf with ReplyMarkup = Some markup }
        | Gallery g -> Gallery { g with ReplyMarkup = Some markup }
        | Message (txt, _) -> Message (txt, Some markup)
        | AudioFile af -> AudioFile af

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


let createUpdateContext () : UpdateContext =
    let config =
        Config.defaultConfig
        |> Config.withReadTokenFromEnv "TELEGRAM_BOT_TOKEN"
    {
        Update = Update.Create(0L)
        Config = config
        Me = User.Create(0L, false, "bot")
    }

let sendRequestAsync (req: 'TReq) (ctx: UpdateContext) =
    async {
        let! res = req |> api ctx.Config
        match res with
        | Ok _ -> ()
        | Error err ->
            Log.Error($"Telegram API request failed: {err.ErrorCode} - {err.Description}")
    }


let private sendMediaWithCaption
    (fileToSend: string)
    (caption: string option)
    (replyMarkup: Markup option)
    (messageId: MessageId)
    (chatId: ChatId)
    (ctx: UpdateContext)
    =
    async {
        let req =
            Req.SendPhoto.Make(
                chatId,
                InputFile.File(fileToSend, File.OpenRead fileToSend),
                parseMode = ParseMode.HTML,
                replyParameters = ReplyParameters.Create(messageId.MessageId, chatId),
                ?caption = caption,
                ?replyMarkup = replyMarkup
            )

        do! sendRequestAsync req ctx
        do! deleteFileAsync fileToSend
    }

// Prepares a video and its thumbnail for sending; opens the streams for upload.
let private buildVideoInputsAsync (videoPath: string) =
    async {
        let! thumbnailPath = getVideoThumbnailAsync videoPath
        let! duration, width, height = getVideoSizeAsync videoPath
        let videoFile = InputFile.File(videoPath, File.OpenRead videoPath)
        return videoFile, thumbnailPath, duration, width, height
    }

let private makeSendVideoRequest (chatId: ChatId) (inputs: InputFile * string option * int64 option * int64 option * int64 option) caption markup (messageId: MessageId) ctx =
    let (videoFile, thumbnailPath, duration, width, height) = inputs
    let thumbnail =
        thumbnailPath |> Option.map (fun thumb -> InputFile.File(thumb, File.OpenRead thumb))

    Req.SendVideo.Make(
        chatId,
        videoFile,
        parseMode = ParseMode.HTML,
        replyParameters = ReplyParameters.Create(messageId.MessageId, chatId),
        showCaptionAboveMedia = true,
        disableNotification = true,
        supportsStreaming = true,
        ?thumbnail = thumbnail,
        ?width = width,
        ?height = height,
        ?duration = duration,
        ?caption = caption,
        ?replyMarkup = markup
    )

let private sendVideoWithThumbnailAsync
    (videoPath: string)
    (caption: string option)
    (replyMarkup: Markup option)
    (messageId: MessageId)
    (chatId: ChatId)
    (ctx: UpdateContext)
    =
    async {
        let! inputs = buildVideoInputsAsync videoPath
        do! sendRequestAsync (makeSendVideoRequest chatId inputs caption replyMarkup messageId ctx) ctx
        do! deleteFileAsync (getThumbnailName videoPath)
        do! deleteFileAsync videoPath
    }

let private sendAudioAsync (audioPath: string) (messageId: MessageId) (chatId: ChatId) (ctx: UpdateContext) =
    async {
        let req =
            Req.SendAudio.Make(
                chatId,
                InputFile.File(audioPath, File.OpenRead audioPath),
                replyParameters = ReplyParameters.Create(messageId.MessageId, chatId),
                disableNotification = true
            )

        do! sendRequestAsync req ctx
        do! deleteFileAsync audioPath
    }

let private createMediaInputAsync (media: GalleryDisplay) =
    async {
        match media with
        | Photo p -> return InputMedia.Photo(InputMediaPhoto.Create("photo", InputFile.File(p, File.OpenRead p), parseMode = ParseMode.HTML))
        | Video v ->
            let! (videoFile, thumbnailPath, duration, width, height) = buildVideoInputsAsync v

            return InputMedia.Video(
                InputMediaVideo.Create(
                    "video",
                    videoFile,
                    parseMode = ParseMode.HTML,
                    ?duration = duration,
                    ?width = width,
                    ?height = height,
                    ?thumbnail = thumbnailPath
                )
            )
    }

let internal getMediaSize (media: GalleryDisplay) =
    let path =
        match media with
        | Photo p -> p
        | Video v -> v
    if File.Exists path then FileInfo(path).Length else 0L

/// Chunks gallery media based on count (max 10) and total size (max 48MB)
let internal chunkGalleryMedia (mediaList: GalleryDisplay list) =
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

let private sendMediaGalleryAsync
    (media: GalleryDisplay list)
    (caption: string option)
    (replyMarkup: Markup option)
    (messageId: MessageId)
    (chatId: ChatId)
    (ctx: UpdateContext)
    =
    async {
        let chunks = chunkGalleryMedia media

        let textReq =
            caption
            |> Option.map (fun msg ->
                let req =
                    Req.SendMessage.Make(
                        chatId,
                        msg,
                        replyParameters = ReplyParameters.Create(messageId.MessageId, chatId),
                        parseMode = ParseMode.HTML,
                        ?replyMarkup = replyMarkup
                    )

                sendRequestAsync req ctx)

        let mediaReqs =
            chunks
            |> List.map (fun chunk ->
                async {
                    match chunk with
                    | [] -> ()
                    // Single item chunks must be sent using the specific photo/video send methods
                    // as sendMediaGroup requires at least 2 items.
                    | [ Photo p ] ->
                        let req =
                            Req.SendPhoto.Make(
                                chatId,
                                InputFile.File(p, File.OpenRead p),
                                parseMode = ParseMode.HTML,
                                replyParameters = ReplyParameters.Create(messageId.MessageId, chatId)
                            )
                        do! sendRequestAsync req ctx
                    | [ Video v ] ->
                        let! inputs = buildVideoInputsAsync v
                        do! sendRequestAsync (makeSendVideoRequest chatId inputs None None messageId ctx) ctx
                    | multipleMedia ->
                        let! g =
                            multipleMedia
                            |> List.map createMediaInputAsync
                            |> Async.Parallel

                        let req =
                            Req.SendMediaGroup.Make(
                                chatId,
                                g,
                                disableNotification = true,
                                replyParameters = ReplyParameters.Create(messageId.MessageId, chatId)
                            )
                        do! sendRequestAsync req ctx
                })

        let requests =
            match textReq with
            | Some textReq -> textReq :: mediaReqs
            | None -> mediaReqs

        for request in requests do
            do! request

        // Correctly delete all temporary files and thumbnails
        let pathsToDelete =
            media
            |> Seq.collect (fun m ->
                match m with
                | Photo p -> seq [ p ]
                | Video v -> seq [ v; getThumbnailName v ])

        for path in pathsToDelete do
            do! deleteFileAsync path
    }

/// Sends a reply back to the chat that produced the original message.
let replyAsync (reply: Reply, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) : Async<unit> =
    async {
        match reply with
        | VideoFile videoFile ->
            let! videoPath = shrinkVideoIfNeededAsync videoFile.File
            let fileInfo = FileInfo videoPath

            if fileInfo.Length > 50L * 1024L * 1024L then
                Log.Information $"File size is greater than 50 MB. Deleting file: %s{videoPath}"
                do! deleteFileAsync videoPath
            else
                let caption = truncateWithEllipsis videoFile.Caption 1024
                do! sendVideoWithThumbnailAsync videoPath caption videoFile.ReplyMarkup messageId chatId ctx

        | AudioFile audioFile ->
            do! sendAudioAsync audioFile messageId chatId ctx

        | Gallery imageGallery ->
            let caption = truncateWithEllipsis imageGallery.Caption 1024

            let! processedMedia =
                imageGallery.Media
                |> List.map (function
                    | Photo p -> async { return Photo p }
                    | Video v ->
                        async {
                            let! shrunk = shrinkVideoIfNeededAsync v
                            return Video shrunk
                        })
                |> Async.Parallel

            match List.ofArray processedMedia with
            | [ singleMedia ] ->
                match singleMedia with
                | Photo p -> do! sendMediaWithCaption p caption imageGallery.ReplyMarkup messageId chatId ctx
                | Video v -> do! sendVideoWithThumbnailAsync v caption imageGallery.ReplyMarkup messageId chatId ctx
            | media -> do! sendMediaGalleryAsync media caption imageGallery.ReplyMarkup messageId chatId ctx

        | Message (message, markup) ->
            let req =
                Req.SendMessage.Make(
                    chatId,
                    message,
                    replyParameters = ReplyParameters.Create(messageId.MessageId, chatId),
                    parseMode = ParseMode.HTML,
                    ?replyMarkup = markup
                )

            do! sendRequestAsync req ctx
    }
