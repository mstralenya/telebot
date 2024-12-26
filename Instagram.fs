module Telebot.Instagram

open System.Net.Http.Json
open System.Text.RegularExpressions
open System
open System.Net.Http
open System.Collections.Generic
open System.Text.Json.Serialization
open Telebot.VideoDownloader

type InstagramXdt = {
    [<JsonPropertyName("video_url")>]
    VideoUrl: string option
}

type Data = {
    [<JsonPropertyName("xdt_shortcode_media")>]
    InstagramXdt: InstagramXdt option
}

type InstagramMediaResponse = {
    [<JsonPropertyName("data")>]
    Data: Data option
}

let private regex = @"(?:https?:\/\/(?:www\.)?instagram\.com\/(?:reels?|p)\/)([\w-]+)"

let private extractReelId (url: string) =
    let m = Regex.Match(url, regex)
    if m.Success then Some m.Groups.[1].Value else None

let getInstagramLinks (input: string option) =
    input
    |> Option.map (fun text ->
        Regex.Matches(text, regex)
        |> Seq.cast<Match>
        |> Seq.map (_.Value)
        |> Seq.toList)
    |> Option.defaultValue List.Empty

let headers = 
    dict [
        "X-FB-Friendly-Name", "PolarisPostActionLoadPostQueryQuery"
        "X-CSRFToken", "RVDUooU5MYsBbS1CNN3CzVAuEP8oHB52"
        "X-IG-App-ID", "1217981644879628"
        "X-FB-LSD", "AVqbxe3J_YA"
        "X-ASBD-ID", "129477"
        "Sec-Fetch-Dest", "empty"
        "Sec-Fetch-Mode", "cors"
        "Sec-Fetch-Site", "same-origin"
        "User-Agent", "Mozilla/5.0 (Linux; Android 11; SAMSUNG SM-G973U) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/14.2 Chrome/87.0.4280.141 Mobile Safari/537.36"
    ]
    
let urlContent =
    [
        KeyValuePair("av", "0")
        KeyValuePair("__d", "www")
        KeyValuePair("__user", "0")
        KeyValuePair("__a", "1")
        KeyValuePair("__req", "3")
        KeyValuePair("__hs", "19624.HYP:instagram_web_pkg.2.1..0.0")
        KeyValuePair("dpr", "3")
        KeyValuePair("__ccg", "UNKNOWN")
        KeyValuePair("__rev", "1008824440")
        KeyValuePair("__s", "xf44ne:zhh75g:xr51e7")
        KeyValuePair("__hsi", "7282217488877343271")
        KeyValuePair("__dyn", "7xeUmwlEnwn8K2WnFw9-2i5U4e0yoW3q32360CEbo1nEhw2nVE4W0om78b87C0yE5ufz81s8hwGwQwoEcE7O2l0Fwqo31w9a9x-0z8-U2zxe2GewGwso88cobEaU2eUlwhEe87q7-0iK2S3qazo7u1xwIw8O321LwTwKG1pg661pwr86C1mwraCg")
        KeyValuePair("__csr", "gZ3yFmJkillQvV6ybimnG8AmhqujGbLADgjyEOWz49z9XDlAXBJpC7Wy-vQTSvUGWGh5u8KibG44dBiigrgjDxGjU0150Q0848azk48N09C02IR0go4SaR70r8owyg9pU0V23hwiA0LQczA48S0f-x-27o05NG0fkw")
        KeyValuePair("__comet_req", "7")
        KeyValuePair("lsd", "AVqbxe3J_YA")
        KeyValuePair("jazoest", "2957")
        KeyValuePair("__spin_r", "1008824440")
        KeyValuePair("__spin_b", "trunk")
        KeyValuePair("__spin_t", "1695523385")
        KeyValuePair("fb_api_caller_class", "RelayModern")
        KeyValuePair("fb_api_req_friendly_name", "PolarisPostActionLoadPostQueryQuery")
        KeyValuePair("server_timestamps", "true")
        KeyValuePair("doc_id", "10015901848480474")
    ]


let getContentPostId postId =
    KeyValuePair("variables", $"{{\"shortcode\":\"{postId}\",\"fetch_comment_count\":null,\"fetch_related_profile_media_count\":null,\"parent_comment_count\":null,\"child_comment_count\":null,\"fetch_like_count\":null,\"fetch_tagged_user_count\":null,\"fetch_preview_comment_count\":null,\"has_threaded_comments\":false,\"hoisted_comment_id\":null,\"hoisted_reply_id\":null}}")

let getMediaIdRequest (reelsId: string) =
    let nameValues = urlContent |> List.append [getContentPostId reelsId]
    let encodedData = new FormUrlEncodedContent(nameValues)
    let requestMessage = new HttpRequestMessage(HttpMethod.Post, Uri("https://www.instagram.com/api/graphql"))
    requestMessage.Content <- encodedData
    headers |> Seq.iter (fun kvp -> requestMessage.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value) |> ignore)
    
    requestMessage

let processInstagramVideoAsync (url: string) =
    async {
        let reelId = extractReelId url

        match reelId with
        | Some rId -> 
            let fileName = $"{Guid.NewGuid}.mp4"
            use mediaIdRequest = getMediaIdRequest rId
            use client = new HttpClient()
            let! mediaIdResponse = client.SendAsync mediaIdRequest |> Async.AwaitTask
            let! apiResponse = mediaIdResponse.Content.ReadFromJsonAsync<InstagramMediaResponse>() |> Async.AwaitTask
            let videoUrl =
                apiResponse.Data
                |> Option.bind (_.InstagramXdt)
                |> Option.bind (_.VideoUrl)
            match videoUrl with
            | Some vUrl ->
                downloadVideoAsync vUrl fileName |> Async.RunSynchronously
                return Some fileName
            | None -> return None
        | None -> return None
    }