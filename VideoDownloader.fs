module Telebot.VideoDownloader

open System.IO
open System.Net.Http
open Telebot.Policies

let downloadVideoAsync(url: string, filePath: string) =
    async {
        use client = new HttpClient()
        let! response = executeWithPolicyAsync (client.GetAsync(url) |> Async.AwaitTask)
        response.EnsureSuccessStatusCode() |> ignore
        let! content = response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
        File.WriteAllBytes(filePath, content)
    }

let deleteVideo(id: string) =
    let filePath = $"{id}.mp4"
    if File.Exists(filePath) then File.Delete(filePath)