module Telebot.TikTok

open System
open Telebot.VideoDownloader
open System.Net.Http
open System.Text.RegularExpressions
open Newtonsoft.Json.Linq

let private fetchTikTokFeedAsync videoId =
    async {
        let url = $"https://api22-normal-c-alisg.tiktokv.com/aweme/v1/feed/?aweme_id=%s{videoId}&iid=7318518857994389254&device_id=7318517321748022790&channel=googleplay&app_name=musical_ly&version_code=300904&device_platform=android&device_type=ASUS_Z01QD&version=9"
        use client = new HttpClient()
        let! response = client.GetAsync(url) |> Async.AwaitTask
        response.EnsureSuccessStatusCode() |> ignore
        let! content = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        return content
    }

let private getVideoUrl (json: string) =
    try
        let doc = JObject.Parse json
        let token = doc.SelectToken("aweme_list[0].video.play_addr.url_list[0]")
        if token <> null then
            Some (token.ToString())
        else
            None
    with
    | _ -> None

let private fetchWithHeadersAsync (url: string option) =
    async {
        match url with
        | Some link -> 
            use client = new HttpClient()
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_10_1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/39.0.2171.95 Safari/537.36")
            let! response = client.GetAsync(link: string) |> Async.AwaitTask
            response.EnsureSuccessStatusCode() |> ignore
            let! content = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            let m = Regex.Match(content, "item_id=(\d+)")
            if m.Success then
                return Some m.Groups[1].Value
            else
                return None
        | None -> return None
    }

let getTikTokLinkIds (input: string option) =
    match input with
    | Some text ->
        let matches = Regex.Matches(text, "http(s)?://(www\.)?(vm.)tiktok.com/(.*)")
        if matches.Count > 0 then
            let matched = matches
                          |> Seq.cast<Match>
                          |> Seq.toList
                          |> List.map (_.Value)
            matched
        else
            List.Empty
    | None -> List.Empty

let processTikTokVideo (url: string option) : option<string> =
    let videoId = fetchWithHeadersAsync(url) |> Async.RunSynchronously
    match videoId with
    | Some id ->
        let result = fetchTikTokFeedAsync id |> Async.RunSynchronously
        match getVideoUrl result with
        | Some url ->
            printfn $"Video URL: %s{url}"
            let fileName = $"{Guid.NewGuid}.mp4"
            downloadVideoAsync url fileName |> Async.RunSynchronously
            printfn $"Video downloaded to %s{fileName}"
            Some fileName
        | None ->
            printfn "Video URL not found"
            None
    | None ->
        printfn "Video ID not found"
        None