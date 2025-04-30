module Telebot.VideoDownloader

open System
open System.IO
open Telebot.Text

let downloadFileAsync(url: string) (filePath: string) =
    async {
        let response = HttpClient.getAsync url
        response.EnsureSuccessStatusCode() |> ignore
        let! content = response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
        File.WriteAllBytes(filePath, content)
    }
    
let downloadMedia url isVideo = async {
        let fileName = $"""{Guid.NewGuid()}.{if isVideo then "mp4" else "jpg"}"""
        do! downloadFileAsync url fileName
        return if isVideo then Video fileName else Photo fileName
    }

let deleteFile(filePath: string) =
    if File.Exists filePath then File.Delete filePath
