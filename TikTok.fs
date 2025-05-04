module Telebot.TikTok

open System
open System.Text.RegularExpressions
open Newtonsoft.Json.Linq
open Serilog
open Telebot.PrometheusMetrics
open Telebot.Text
open Telebot.Text.Reply
open Telebot.VideoDownloader

let private fetchTikTokFeedAsync videoId =
    async {
        let url = $"https://api22-normal-c-alisg.tiktokv.com/aweme/v1/feed/?aweme_id=%s{videoId}&iid=7318518857994389254&device_id=7318517321748022790&channel=googleplay&app_name=musical_ly&version_code=300904&device_platform=android&device_type=ASUS_Z01QD&version=9"
        let response = HttpClient.getAsync url
        return response.Content.ReadAsStringAsync() |> Async.AwaitTask
    }

let private getVideoUrl json =
    try
        let doc = JObject.Parse json
        doc.SelectToken "aweme_list[0].video.play_addr.url_list[0]"
        |> Option.ofObj
        |> Option.map _.ToString()
    with _ -> None

let private fetchWithHeadersAsync (url: string) =
    async {
        let response = HttpClient.getAsync url
        response.EnsureSuccessStatusCode() |> ignore
        let! content = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        let m = Regex.Match(content, "item_id=(\d+)")
        return if m.Success then Some m.Groups[1].Value else None
    }

let private tikTokeRegex = Regex(@"http(s)?://(www\.)?(\w+\.)?tiktok.com/(.*)", RegexOptions.Compiled)

let getTikTokLinks (_: string option) = getLinks tikTokeRegex

let getTikTokReply (url: string) =
    async {
        let! videoId = fetchWithHeadersAsync url
        match videoId with
        | Some id when not (String.IsNullOrEmpty id) ->
            let! feed = fetchTikTokFeedAsync id |> Async.RunSynchronously
            let result = getVideoUrl feed
                       |> Option.map (fun videoUrl ->
                           let fileName = $"tt_{id}_{Guid.NewGuid()}.mp4"
                           downloadFileAsync videoUrl fileName |> Async.RunSynchronously
                           Log.Information $"Video downloaded to %s{fileName}"
                           createVideoFile fileName)
            tiktokSuccessMetric.Inc()
            return result
        | _ ->
            tiktokMissingVideoIdMetric.Inc()
            Log.Information "Video ID not found"
            return None
    }
    |> Async.RunSynchronously
