module Telebot.VideoDownloader

open System
open System.IO
open Telebot.PrometheusMetrics
open Telebot.Text

let downloadFileAsync (url: string) (filePath: string) =
    async {
        let response = HttpClient.getAsync url
        response.EnsureSuccessStatusCode() |> ignore
        let! content = response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
        File.WriteAllBytes(filePath, content)
        downloadCounter.Inc()
    }

let downloadMedia url isVideo =
    async {
        let name = Guid.NewGuid()
        let extension = if isVideo then "mp4" else "jpg"
        let fileName = $"{name}.{extension}"
        do! downloadFileAsync url fileName
        return if isVideo then Video fileName else Photo fileName
    }

let deleteFile (filePath: string) =
    if File.Exists filePath then
        File.Delete filePath

    deleteCounter.Inc()
