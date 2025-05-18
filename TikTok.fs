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

let private fetchTikTokUrl url token=
    let response = HttpClient.getAsync url
    match response.StatusCode with
        | HttpStatusCode.OK ->
            let json = response.Content.ReadAsStringAsync()
                       |> Async.AwaitTask
                       |> Async.RunSynchronously
                       |> JObject.Parse
            json.SelectToken token
                |> Option.ofObj
                |> Option.map _.ToString()
        | _ -> None

let private fetchTikTokVideoUrl url = fetchTikTokUrl url "data.play"
let private fetchTikTokAudioUrl url = fetchTikTokUrl url "data.music"

let private tikTokRegex = Regex(@"http(s)?://(www\.)?(\w+\.)?tiktok.com/(.*)", RegexOptions.Compiled)

let getTikTokAudioLinks (message: string option) =
    match message with
    | Some text when text.Contains "audio" -> getLinks tikTokRegex
    | _ -> fun _ -> List.empty

let getTikTokVideoLinks (message: string option) =
    match message with
    | Some text when not (text.Contains "audio") -> getLinks tikTokRegex
    | _ -> fun _ -> List.empty

let getTikTokReply (isVideo: bool) (url: string) =
    async {
        let apiUrl = $"https://www.tikwm.com/api/?url={url}?hd=1"
        let fileUrl = if isVideo then fetchTikTokVideoUrl apiUrl else fetchTikTokAudioUrl apiUrl
        let fileName = $"tt_{id}_{Guid.NewGuid()}.mp4"
        match fileUrl with
            | Some fUrl ->
                downloadFileAsync fUrl fileName |> Async.RunSynchronously
                match isVideo with
                    | true -> return Some (createVideoFile fileName)
                    | false -> return Some (createAudioFile fileName)
            | None ->
                return Some (createMessage "Couldn't find tiktok video")
    }
    |> Async.RunSynchronously
