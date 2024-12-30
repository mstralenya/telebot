module Telebot.TikTok

open System
open System.Net.Http
open System.Text.RegularExpressions
open Newtonsoft.Json.Linq
open Serilog
open Telebot.Text
open Telebot.VideoDownloader
open Telebot.Policies

let private fetchTikTokFeedAsync videoId =
    async {
        let url = $"https://api22-normal-c-alisg.tiktokv.com/aweme/v1/feed/?aweme_id=%s{videoId}&iid=7318518857994389254&device_id=7318517321748022790&channel=googleplay&app_name=musical_ly&version_code=300904&device_platform=android&device_type=ASUS_Z01QD&version=9"
        use client = new HttpClient()
        let! response = executeWithPolicyAsync (client.GetAsync(url) |> Async.AwaitTask)
        response.EnsureSuccessStatusCode() |> ignore
        
        return response.Content.ReadAsStringAsync() |> Async.AwaitTask
    }

let private getVideoUrl json =
    try
        let doc = JObject.Parse json
        doc.SelectToken("aweme_list[0].video.play_addr.url_list[0]")
        |> Option.ofObj
        |> Option.map (_.ToString())
    with _ -> None

let private fetchWithHeadersAsync (url: string) =
    async {
        use client = new HttpClient()
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_10_1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/39.0.2171.95 Safari/537.36")
        let! response = executeWithPolicyAsync (client.GetAsync(url) |> Async.AwaitTask)
        response.EnsureSuccessStatusCode() |> ignore
        let! content = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        return Regex.Match(content, "item_id=(\d+)")
               |> Option.ofObj
               |> Option.bind (fun m -> Some m.Groups[1].Value)
    }

let getTikTokLinks (_: string option) = getLinks @"http(s)?://(www\.)?(\w+\.)?tiktok.com/(.*)"

let getTikTokReply (url: string) =
    async {
        let! videoId = fetchWithHeadersAsync url
        match videoId with
        | Some id ->
            let! feed = fetchTikTokFeedAsync id |> Async.RunSynchronously
            return getVideoUrl feed
                   |> Option.map (fun videoUrl ->
                       let fileName = $"tt_{id}_{Guid.NewGuid()}.mp4"
                       downloadVideoAsync videoUrl fileName |> Async.RunSynchronously
                       Log.Information $"Video downloaded to %s{fileName}"
                       VideoFile (fileName, ""))
        | None ->
            Log.Information "Video ID not found"
            return None
    }
    |> Async.RunSynchronously