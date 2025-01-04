module Telebot.Twitter

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions
open System.Web
open Newtonsoft.Json.Linq
open Telebot.Text
open Telebot.VideoDownloader

type VideoInfo = {
    [<JsonPropertyName("content_type")>]
    ContentType: string
    [<JsonPropertyName("url")>]
    Url: string
    [<JsonPropertyName("bitrate")>]
    Bitrate: int option
}

let private twitterRegex = @"https://(x|twitter).com/.*/status/(\d+)"

let private client = new HttpClient()

// Common headers
let private commonHeaders = [
    "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/92.0.4515.107 Safari/537.36"
    "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
    "Accept-Language", "en-us,en;q=0.5"
    "Sec-Fetch-Mode", "navigate"
    "Authorization", "Bearer AAAAAAAAAAAAAAAAAAAAANRILgAAAAAAnNwIzUejRCOuH5E6I8xnZz4puTs%3D1Zv7ttfk8LF81IUq16cHjhLTvJu4FA33AGWWjCpTnA"
]

// Helper function to set headers
let private setHeaders (headers: (string * string) list) (request: HttpRequestMessage) =
    headers |> List.iter (fun (key, value) -> request.Headers.Add(key, value))
    request

// Request 1: Activate guest token
let activateGuestToken () =
    let url = "https://api.twitter.com/1.1/guest/activate.json"
    let headers = ["Host", "api.twitter.com"] @ commonHeaders
    let request = new HttpRequestMessage(HttpMethod.Post, url)
    setHeaders headers request |> ignore
    let response = client.SendAsync(request).Result
    let jsonResponse = response.Content.ReadAsStringAsync().Result
    let jsonDoc = JsonDocument.Parse(jsonResponse)
    let guestToken = jsonDoc.RootElement.GetProperty("guest_token").GetString()
    guestToken

let generateTwitterApiUrl (tweetId: string) =
    let baseUrl = "https://twitter.com/i/api/graphql/2ICDjqPd81tulZcYrtpTuQ/TweetResultByRestId"
    
    let variables = 
        $"""{{"tweetId":"%s{tweetId}","withCommunity":false,"includePromotedContent":false,"withVoice":false}}"""
    
    let features = 
        """{"creator_subscriptions_tweet_preview_api_enabled":true,"tweetypie_unmention_optimization_enabled":true,"responsive_web_edit_tweet_api_enabled":true,"graphql_is_translatable_rweb_tweet_is_translatable_enabled":true,"view_counts_everywhere_api_enabled":true,"longform_notetweets_consumption_enabled":true,"responsive_web_twitter_article_tweet_consumption_enabled":false,"tweet_awards_web_tipping_enabled":false,"freedom_of_speech_not_reach_fetch_enabled":true,"standardized_nudges_misinfo":true,"tweet_with_visibility_results_prefer_gql_limited_actions_policy_enabled":true,"longform_notetweets_rich_text_read_enabled":true,"longform_notetweets_inline_media_enabled":true,"responsive_web_graphql_exclude_directive_enabled":true,"verified_phone_label_enabled":false,"responsive_web_media_download_video_enabled":false,"responsive_web_graphql_skip_user_profile_image_extensions_enabled":false,"responsive_web_graphql_timeline_navigation_enabled":true,"responsive_web_enhance_cards_enabled":false}"""
    
    let fieldToggles = 
        """{"withArticleRichContentState":false}"""
    
    let queryParams = 
        [ "variables", variables
          "features", features
          "fieldToggles", fieldToggles ]
        |> List.map (fun (k, v) -> $"%s{k}=%s{HttpUtility.UrlEncode(v)}")
        |> String.concat "&"
    
    $"%s{baseUrl}?%s{queryParams}"

// Request 2: Get tweet result by REST ID
let getTweetResultByRestId guestToken tweetId =
    let url = generateTwitterApiUrl tweetId
    let headers = [
        "Host", "twitter.com"
        "Cookie", "guest_id=v1%3A173548179257147512"
        "X-Guest-Token", guestToken] @ commonHeaders
    let request = new HttpRequestMessage(HttpMethod.Get, url)
    setHeaders headers request |> ignore
    let response = client.SendAsync(request).Result
    response.Content.ReadAsStringAsync().Result

let getJsonPathValue (json: string) (path: string) =
    let jsonDocument = JObject.Parse(json)
    jsonDocument.SelectToken(path)
    |> Option.ofObj
    |> Option.map (_.ToString())

// Request 3: Get video
let getVideoFile(tweetResult: string) =
    let element = getJsonPathValue tweetResult "data.tweetResult.result.legacy.entities.media[0].video_info.variants"

    match element with
    | Some stringElement ->
        let jsonArray = JsonDocument.Parse(stringElement)
       // Deserialize the JSON into a list of VideoInfo
        let videoInfos =
            jsonArray.RootElement.EnumerateArray()
            |> Seq.map (fun element -> JsonSerializer.Deserialize<VideoInfo>(element, JsonSerializerOptions(IncludeFields = true)))
            |> Seq.toList

        // Select the URL with the best bitrate
        videoInfos
            |> List.choose (fun vi -> vi.Bitrate |> Option.map (fun br -> (br, vi.Url))) // Keep only items with a bitrate
            |> List.sortByDescending fst // Sort by bitrate in descending order
            |> List.tryHead // Get the first item (highest bitrate)
        |> Option.map snd // Extract the URL
    | None -> None

let private computeHash (input: string) =
    using (SHA256.Create()) (fun sha256 ->
        let bytes = Encoding.UTF8.GetBytes(input)
        let hashBytes = sha256.ComputeHash(bytes)
        BitConverter.ToString(hashBytes).Replace("-", "").ToLower()
    )

let getTwitterReply (url: string) =
    let guestToken = activateGuestToken()
    let tweetId = Regex.Match(url, twitterRegex).Groups.[2].Value
    let tweetResult = getTweetResultByRestId guestToken tweetId
    let videoFile = getVideoFile tweetResult 
    match videoFile with
    | Some videoFile -> 
        // Process the video file
        let hash = computeHash videoFile
        let fileName = $"t_{hash}_{Guid.NewGuid()}.mp4"
        downloadVideoAsync videoFile fileName |> Async.RunSynchronously
        let author = getJsonPathValue tweetResult "data.tweetResult.result.core.user_results.result.legacy.name"
        let authorHandle = getJsonPathValue tweetResult "data.tweetResult.result.core.user_results.result.legacy.screen_name"
        let text = getJsonPathValue tweetResult "data.tweetResult.result.legacy.full_text"
        let replyText = 
            match author, authorHandle, text with
            | Some a, Some ah, Some t -> $"<b>{a}</b> <i>(@​{ah})</i>: <blockquote>{t}</blockquote>"
            | Some a, Some ah, _ -> $"<b>{a}</b> <i>(@​{ah})</i>:"
            | _ -> ""
        Some (VideoFile (fileName, Some replyText, None, None))
    | None ->
        let previewLink = Regex.Replace(url, "http(s)?://(www\.)?(twitter|x).com(/.*)?", "https://fixupx.com$4")
        Some (Message previewLink)

let getTwitterLinks (_: string option) = getLinks twitterRegex