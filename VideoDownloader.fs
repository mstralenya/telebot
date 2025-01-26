module Telebot.VideoDownloader

open System.IO
open System.Net.Http
open System.Threading.Tasks
open Telebot.Policies

let downloadFileAsync(url: string) (filePath: string) =
    async {
        use client = new HttpClient()
        let! response = executeWithPolicyAsync (client.GetAsync(url) |> Async.AwaitTask)
        response.EnsureSuccessStatusCode() |> ignore
        let! content = response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
        File.WriteAllBytes(filePath, content)
    }

let deleteFile(filePath: string) =
    if File.Exists(filePath) then File.Delete(filePath)