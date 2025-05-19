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

let getJsonToken (json: JObject) token =
    json.SelectToken token
    |> Option.ofObj
    |> Option.map _.ToString()
    |> Option.defaultValue ""

let private fetchTikTokUrl url =
    let response = HttpClient.getAsync $"https://www.tikwm.com/api/?url={url}?hd=1"

    match response.StatusCode with
    | HttpStatusCode.OK ->
        response.Content.ReadAsStringAsync()
        |> Async.AwaitTask
        |> Async.RunSynchronously
        |> JObject.Parse
        |> Some
    | _ -> None

let private tikTokRegex =
    Regex(@"http(s)?://(www\.)?(\w+\.)?tiktok.com/(.*)", RegexOptions.Compiled)

let getTikTokAudioLinks (message: string option) =
    match message with
    | Some text when text.Contains "audio" -> getLinks tikTokRegex
    | _ -> fun _ -> List.empty

let getTikTokVideoLinks (message: string option) =
    match message with
    | Some text when not (text.Contains "audio") -> getLinks tikTokRegex
    | _ -> fun _ -> List.empty

let getAudioReply url =
    let apiResponse = fetchTikTokUrl url

    match apiResponse with
    | Some jObject ->
        let audioUrl = getJsonToken jObject "data.music"

        let fileName =
            getJsonToken jObject "data.music_info.title" |> fun title -> $"tt_{title}.mp3"

        downloadFileAsync audioUrl fileName |> Async.RunSynchronously

        Some(createAudioFile fileName)
    | None -> Some(createMessage "Failed to download tiktok audio")

let getVideoReply url =
    let apiResponse = fetchTikTokUrl url

    match apiResponse with
    | Some jObject ->
        let videoUrl = getJsonToken jObject "data.play"

        let fileName =
            getJsonToken jObject "data.id" |> fun title -> $"tt_{title}.mp4"

        downloadFileAsync videoUrl fileName |> Async.RunSynchronously

        Some(createVideoFile fileName)
    | None -> Some(createMessage "Failed to download tiktok video")

let getTikTokReply (isVideo: bool) (url: string) =
    match isVideo with
    | true -> getVideoReply url
    | false -> getAudioReply url
