module Telebot.Youtube

open System
open System.Text.RegularExpressions
open Serilog
open Telebot.Bus
open Telebot.Handlers
open Telebot.Helpers
open Telebot.Messages
open Telebot.Text
open Telebot.Text.Reply
open YoutubeExplode
open YoutubeExplode.Converter
open YoutubeExplode.Videos.Streams
module private Youtube =
    let private youtubeRegex =
        Regex(
            @"https:\/\/(youtu\.be\/[a-zA-Z0-9_-]+|(?:www\.)?youtube\.com\/(watch\?v=[a-zA-Z0-9_-]+|shorts\/[a-zA-Z0-9_-]+))",
            RegexOptions.Compiled
        )
    let getYoutubeLinks (message: string option) = getLinks youtubeRegex message

    let private getVideoStream (streamManifest: StreamManifest) (maxSize: FileSize) =
        let h264Stream =
            streamManifest.GetVideoOnlyStreams()
            |> Seq.filter (fun c ->
                c.Container = Container.Mp4
                && c.VideoCodec.StartsWith "avc1"
                && c.Size.Bytes < maxSize.Bytes)
            |> tryMaxBy _.Bitrate.KiloBitsPerSecond

        match h264Stream with
        | Some stream -> Some stream
        | None ->
            streamManifest.GetVideoOnlyStreams()
            |> Seq.filter (fun c -> c.Container = Container.WebM && c.Size.Bytes < maxSize.Bytes)
            |> tryMaxBy _.Bitrate.KiloBitsPerSecond

    let getYoutubeReply (url: string) =
        async {
            let youtube = YoutubeClient()

            // Get video metadata
            let! video = youtube.Videos.GetAsync url |> toTask |> Async.AwaitTask

            let fileName = $"yt_{video.Id}_{Guid.NewGuid()}.mp4"
            Log.Information $"Downloading video: %s{video.Title}"

            // Get available streams for the video
            let! streamManifest = youtube.Videos.Streams.GetManifestAsync url |> toTask |> Async.AwaitTask

            Log.Information
                $"Video Stream options: %A{streamManifest.GetVideoOnlyStreams()
                                           |> Seq.toList
                                           |> List.map (fun x -> x.Container, x.Bitrate, x.Size, x.VideoResolution, x.VideoCodec)}"

            Log.Information
                $"Audio Stream options: %A{streamManifest.GetAudioOnlyStreams()
                                           |> Seq.toList
                                           |> List.map (fun x -> x.Container, x.Bitrate, x.Size, x.AudioCodec)}"

            // Get the best video stream (e.g., highest quality)
            let audioStream =
                streamManifest.GetAudioOnlyStreams()
                |> Seq.filter (fun c -> c.Size.MegaBytes < 8)
                |> tryMaxBy _.Bitrate.KiloBitsPerSecond

            let audioSize = audioStream |> Option.map _.Size |> Option.defaultValue (FileSize 1)

            let maxVideoSizeBytes = FileSize(1024L * 1024L * 50L - audioSize.Bytes)
            let videoStream = getVideoStream streamManifest maxVideoSizeBytes

            match videoStream, audioStream with
            | Some videoS, Some audioS ->
                // Download the video
                Log.Information $"Downloading stream: %s{videoS.Url}"

                let streams: IStreamInfo list = [ videoS; audioS ]

                let conversionRequest =
                    ConversionRequestBuilder(fileName).SetPreset(ConversionPreset.Fast).SetContainer(Container.Mp4).Build()

                youtube.Videos.DownloadAsync(streams, conversionRequest)
                |> _.AsTask() // Convert ValueTask to Task
                |> Async.AwaitTask // Await the Task
                |> Async.RunSynchronously

                Log.Information $"Video downloaded successfully: %s{fileName}, resolution {videoS.VideoResolution}, fileSize {videoS.Size}"

                let title = Some video.Title
                let reply = createVideoFileWithCaption fileName title

                return Some reply
            | _ ->
                Log.Warning "No suitable streams found for the video. Download aborted."

                let message =
                    createMessage "Cannot download video due size limits - max size is 50 MiB"

                return Some message

        }
        |> Async.RunSynchronously


type YoutubeLinksHandler() =
    inherit BaseHandler()
    member private this.extractYoutubeLinks =
        createLinkExtractor Youtube.getYoutubeLinks YoutubeMessage
    member this.Handle(msg: UpdateMessage) =
        this.extractYoutubeLinks msg |> List.map publishToBus |> ignore
    member this.Handle(msg: YoutubeMessage) =
        this.processLink msg Youtube.getYoutubeReply
