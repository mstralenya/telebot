module Telebot.Twitter

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Telebot.Text
open Telebot.VideoDownloader
open Telebot.LoggingHandler
// Define the data structures
type Size = {
    height: int
    width: int
}

type MediaExtended = {
    altText: string option
    size: Size
    thumbnail_url: string
    mediaType: string
    url: string
}

type Qrt = {
    allSameType: bool
    article: string option
    combinedMediaUrl: string option
    communityNote: string option
    conversationID: string
    date: string
    date_epoch: int64
    hasMedia: bool
    hashtags: string list
    lang: string
    likes: int
    mediaURLs: string list
    media_extended: MediaExtended list
    pollData: string option
    possibly_sensitive: bool
    qrtURL: string
    replies: int
    retweets: int
    text: string option
    tweetID: string
    tweetURL: string
    user_name: string
    user_profile_image_url: string
    user_screen_name: string
}

type Tweet = {
    date_epoch: int64
    hashtags: string list
    likes: int
    mediaURLs: string list
    media_extended: MediaExtended list
    pollData: string option
    possibly_sensitive: bool
    qrt: Qrt option
    qrtURL: string
    replies: int
    retweets: int
    text: string option
    tweetID: string
    tweetURL: string
    user_name: string
    user_profile_image_url: string
    user_screen_name: string
}


// Function to replace the domain in the URL
let replaceDomain (url: string) =
    url.Replace("https://x.com/", "https://api.vxtwitter.com/")

// Function to fetch JSON response from the URL
let fetchJsonAsync (url: string) =
    async {
        let handler = LoggingHandler(new HttpClientHandler())
        let httpClient = new HttpClient(handler)
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3")
        let! response = httpClient.GetStringAsync(url) |> Async.AwaitTask
        return response
    }

// Function to parse JSON response into Tweet structure
let parseTweet (json: string) =
    let options = JsonSerializerOptions()
    // options.Converters.Add(JsonFSharpConverter())
    options.PropertyNameCaseInsensitive <- true
    JsonSerializer.Deserialize<Tweet>(json, options)

// Main function to process the URL and return the Tweet structure
let getTweetFromUrlAsync (url: string) =
    async {
        let newUrl = replaceDomain url
        let! json = fetchJsonAsync newUrl
        return parseTweet json
    }

let private computeHash (input: string) =
    using (SHA256.Create()) (fun sha256 ->
        let bytes = Encoding.UTF8.GetBytes(input)
        let hashBytes = sha256.ComputeHash(bytes)
        BitConverter.ToString(hashBytes).Replace("-", "").ToLower()
    )
 
let private twitterRegex = Regex(@"https://(x|twitter).com/.*/status/(\d+)", RegexOptions.Compiled)

let downloadMedia url isVideo = async {
        let fileName = $"""t_{Guid.NewGuid()}.{(if isVideo then "mp4" else "jpg")}"""
        do! downloadFileAsync url fileName
        return if isVideo then Video fileName else Photo fileName
    }

// Function to process a list of URLs and return an array of results
let processUrls (urls: string list) =
    urls
    |> List.map (fun url ->
        let isVideo = url.EndsWith(".mp4")
        downloadMedia url isVideo)
    |> Async.Parallel
    |> Async.RunSynchronously
    |> Array.toList

let mergeMediaUrls (tweet: Tweet) =
    match tweet.qrt with
    | Some qrt -> tweet.mediaURLs @ qrt.mediaURLs  // Concatenate the two lists if qrt is Some
    | None -> tweet.mediaURLs  // If qrt is None, just return the main tweet's mediaURLs

let getTwitterReply (url: string) =
    let tweet = getTweetFromUrlAsync url |> Async.RunSynchronously
    let replyText =
        match tweet.user_screen_name, tweet.user_name, tweet.text, tweet.qrt with
        | ah, a, Some t, Some qrt -> Some $"""
<b>{a}</b> <i>(@​{ah})</i>:
<blockquote>{t}</blockquote>

Quoting <b>{qrt.user_name}</b> <i>@​{qrt.user_screen_name}</i>:
<blockquote>{qrt.text |> Option.defaultValue ""}</blockquote>
"""
        | ah, a, Some t, _ -> Some $"<b>{a}</b> <i>(@​{ah})</i>:\n <blockquote>{t}</blockquote>"
        | ah, a, _, _ -> Some $"<b>{a}</b> <i>(@​{ah})</i>:"

    let mediaUrls = mergeMediaUrls tweet
    let gallery = processUrls mediaUrls
    match gallery.Length with
    | i when i > 0 -> Some (Reply.createGallery gallery replyText)
    | _ -> Some (Reply.createMessage replyText.Value)

let getTwitterLinks (_: string option) = getLinks twitterRegex
