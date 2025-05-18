module Telebot.TikTok

open System
open System.Net
open System.Text.RegularExpressions
open Newtonsoft.Json.Linq
open Serilog
open Telebot.PrometheusMetrics
open Telebot.Text
open Telebot.Text.Reply
open Telebot.VideoDownloader

let private fetchTikTokVideoUrl url =
    let response = HttpClient.getAsync url
    match response.StatusCode with
        | HttpStatusCode.OK ->
            let json = response.Content.ReadAsStringAsync()
                       |> Async.AwaitTask
                       |> Async.RunSynchronously
                       |> JObject.Parse
            json.SelectToken "data.play"
                |> Option.ofObj
                |> Option.map _.ToString()
        | _ -> None

let private tikTokeRegex = Regex(@"http(s)?://(www\.)?(\w+\.)?tiktok.com/(.*)", RegexOptions.Compiled)

let getTikTokLinks (_: string option) = getLinks tikTokeRegex

let getTikTokReply (url: string) =
    async {
        let url = $"https://www.tikwm.com/api/?url={url}?hd=1"
        let videoUrl = fetchTikTokVideoUrl url
        let fileName = $"tt_{id}_{Guid.NewGuid()}.mp4"
        match videoUrl with
            | Some vUrl ->
                downloadFileAsync vUrl fileName |> Async.RunSynchronously
                return Some (createVideoFile fileName)
            | None ->
                return Some (createMessage "Couldn't find tiktok video")
    }
    |> Async.RunSynchronously
