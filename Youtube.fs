module Telebot.Youtube

open System
open System.Threading.Tasks
open Serilog
open Telebot.Text
open YoutubeExplode
open YoutubeExplode.Converter
open YoutubeExplode.Videos.Streams

// Helper function to convert ValueTask<T> to Task<T>
let toTask (valueTask: ValueTask<'T>) = valueTask.AsTask()

let getYoutubeLinks (_: string option) = getLinks @"https:\/\/(youtu\.be\/[a-zA-Z0-9_-]+|www\.youtube\.com\/(watch\?v=[a-zA-Z0-9_-]+|shorts\/[a-zA-Z0-9_-]+))"


let getYoutubeReply (url: string) =
    async {
    let youtube = YoutubeClient()

    // Get video metadata
    let! video = youtube.Videos.GetAsync(url) |> toTask |> Async.AwaitTask

    // Check if the video is longer than 5 minutes
    if video.Duration.Value > TimeSpan.FromMinutes(5.0) then
        Log.Information "The video is longer than 5 minutes. Download aborted."
        return None
    else
        let fileName = $"yt_{video.Id}_{Guid.NewGuid()}.mp4"
        Log.Information $"Downloading video: %s{video.Title}"

        // Get available streams for the video
        let! streamManifest = youtube.Videos.Streams.GetManifestAsync(url) |> toTask |> Async.AwaitTask

        // Get the best video stream (e.g., highest quality)
        let videoStream = streamManifest.GetVideoOnlyStreams()
                          |> Seq.filter (fun c -> c.Container = Container.Mp4)
                          |> Seq.maxBy (_.Bitrate.KiloBitsPerSecond)
        let audioStream = streamManifest.GetAudioOnlyStreams()
                          |> Seq.maxBy (_.Bitrate.KiloBitsPerSecond)

        // Download the video
        Log.Information $"Downloading stream: %s{videoStream.Url}"

        let streams : IStreamInfo list = [videoStream; audioStream]
        let conversionRequest = ConversionRequestBuilder(fileName).Build()
        youtube.Videos.DownloadAsync(streams, conversionRequest)
            |> _.AsTask() // Convert ValueTask to Task
            |> Async.AwaitTask // Await the Task
            |> Async.RunSynchronously

        Log.Information $"Video downloaded successfully: %s{fileName}"

        return Some (VideoFile (fileName, video.Title))
    } |> Async.RunSynchronously