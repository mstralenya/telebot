﻿module Telebot.Twitter

open System.Net.Http.Json
open System.Text.Json
open System.Text.RegularExpressions
open Telebot.Text
open Telebot.TwitterData
open Telebot.VideoDownloader

// Function to replace the domain in the URL
let replaceDomain (url: string) =
    url.Replace("https://x.com/", "https://api.vxtwitter.com/")

// Main function to process the URL and return the Tweet structure
let getTweetFromUrlAsync (url: string) =
    async {
        let newUrl = replaceDomain url
        let response = HttpClient.getAsync newUrl
        let options = JsonSerializerOptions()
        options.PropertyNameCaseInsensitive <- true
        let result = response.Content.ReadFromJsonAsync<Tweet>(options) |> Async.AwaitTask |> Async.RunSynchronously
        return result
    }
 
let private twitterRegex = Regex(@"https://(x|twitter).com/.*/status/(\d+)", RegexOptions.Compiled)

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
