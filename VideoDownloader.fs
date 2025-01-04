module Telebot.VideoDownloader

open System.IO
open System.Net.Http
open System.Threading.Tasks
open Telebot.Policies

let downloadVideoAsync(url: string) (filePath: string) =
    async {
        use client = new HttpClient()
        let! response = executeWithPolicyAsync (client.GetAsync(url) |> Async.AwaitTask)
        response.EnsureSuccessStatusCode() |> ignore
        let! content = response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
        File.WriteAllBytes(filePath, content)
    }

let downloadImageAsync (url: string) (filePath: string) : Task =
    task {
        use httpClient = new HttpClient()
        let! imageBytes = httpClient.GetByteArrayAsync(url)
        do! File.WriteAllBytesAsync(filePath, imageBytes)
    }

let deleteFile(filePath: string) =
    if File.Exists(filePath) then File.Delete(filePath)