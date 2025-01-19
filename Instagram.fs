module Telebot.Instagram

open System.Net.Http.Json
open System.Text.RegularExpressions
open System
open System.Net.Http
open System.Text.Json.Serialization
open System.Collections.Generic
open Telebot.Policies
open Telebot.Text
open Telebot.VideoDownloader
open System.Text.Json
open System.IO

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

let private loadJsonFromFile<'T> (filePath: string) =
    let json = File.ReadAllText(filePath)
    JsonSerializer.Deserialize<'T>(json)

let private headers = 
    loadJsonFromFile<Dictionary<string, string>>("igHeaders.json")

let private urlContent = 
    loadJsonFromFile<KeyValuePair<string, string>[]>("igUrlContent.json")
    |> Array.toList

let private igRegex = @"(?:https?:\/\/(?:www\.)?instagram\.com\/(?:[^\/]+\/)?(?:reels?|p)\/)([\w-]+)"

let private extractReelId url =
    let m = Regex.Match(url, igRegex)
    if m.Success then Some m.Groups.[1].Value else None

let private getContentPostId postId =
    KeyValuePair("variables", $"{{\"shortcode\":\"{postId}\",\"fetch_comment_count\":null,\"fetch_related_profile_media_count\":null,\"parent_comment_count\":null,\"child_comment_count\":null,\"fetch_like_count\":null,\"fetch_tagged_user_count\":null,\"fetch_preview_comment_count\":null,\"has_threaded_comments\":false,\"hoisted_comment_id\":null,\"hoisted_reply_id\":null}}")

let private getMediaIdRequest reelsId =
    let nameValues = urlContent @ [getContentPostId reelsId]
    let encodedData = new FormUrlEncodedContent(nameValues)
    let requestMessage = new HttpRequestMessage(HttpMethod.Post, Uri("https://www.instagram.com/api/graphql"))
    requestMessage.Content <- encodedData
    headers |> Seq.iter (fun kvp -> requestMessage.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value) |> ignore)
    requestMessage

let getInstagramReply (url: string) =
    match extractReelId url with
        | Some rId ->
            let fileName = $"tt_{rId}_{Guid.NewGuid()}.mp4"
            use mediaIdRequest = getMediaIdRequest rId
            use client = new HttpClient()
            let mediaIdResponse = executeWithPolicyAsync (client.SendAsync mediaIdRequest |> Async.AwaitTask) |> Async.RunSynchronously
            let apiResponse = mediaIdResponse.Content.ReadFromJsonAsync<InstagramMediaResponse>().Result
            apiResponse.Data
            |> Option.bind (_.InstagramXdt)
            |> Option.bind (_.VideoUrl)
            |> Option.map (fun vUrl ->
                downloadVideoAsync vUrl fileName |> Async.RunSynchronously
                Reply.createVideoFile fileName)
        | None -> None

let getInstagramLinks (_: string option) = getLinks igRegex
