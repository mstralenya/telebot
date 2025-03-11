module Telebot.Twitter

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Telebot.Text
open Telebot.VideoDownloader
// Define the data structures
type Size = {
    height: int
    width: int
}

type MediaExtended = {
    altText: string
    size: Size
    thumbnail_url: string
    mediaType: string
    url: string
    duration_millis: int option
}

type Tweet = {
    date_epoch: int64
    hashtags: string list
    likes: int
    mediaURLs: string list
    media_extended: MediaExtended list
    replies: int
    retweets: int
    text: string option
    tweetID: string
    tweetURL: string
    user_name: string
    user_screen_name: string
}

// Function to replace the domain in the URL
let replaceDomain (url: string) =
    url.Replace("https://x.com/", "https://api.vxtwitter.com/")

// Function to fetch JSON response from the URL
let fetchJsonAsync (url: string) =
    async {
        use httpClient = new HttpClient()
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

let getTwitterReply (url: string) =
    let tweet = getTweetFromUrlAsync url |> Async.RunSynchronously
    let replyText =
        match tweet.user_screen_name, tweet.user_name, tweet.text with
        | a, ah, Some t -> Some $"{a} (@​{ah}):\n {t}"
        | a, ah, _ -> Some $"{a} (@​{ah})"
        
    let gallery = processUrls tweet.mediaURLs
    match gallery.Length with
    | i when i > 0 -> Some (Reply.createGallery gallery replyText)
    | _ -> Some (Reply.createMessage replyText.Value)

let getTwitterLinks (_: string option) = getLinks twitterRegex