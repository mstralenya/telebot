module Telebot.Instagram

open System.Net.Http.Json
open System.Text.RegularExpressions
open System
open System.Net.Http
open System.Text.Json.Serialization
open System.Collections.Generic
open System.Threading
open Telebot.Policies
open Telebot.Text
open Telebot.VideoDownloader
open System.Text.Json
open System.IO

type PostType =
    | Reel of string
    | Post of string
    | Nothing


[<CLIMutable>]
type CaptionNode = {
    [<JsonPropertyName("created_at")>]
    CreatedAt: string
    [<JsonPropertyName("text")>]
    Text: string
    [<JsonPropertyName("id")>]
    Id: string
}

[<CLIMutable>]
type CaptionEdge = {
    [<JsonPropertyName("node")>]
    Node: CaptionNode
}

[<CLIMutable>]
type EdgeMediaToCaption = {
    [<JsonPropertyName("edges")>]
    Edges: CaptionEdge list
}

type InstagramXdt = {
    [<JsonPropertyName("video_url")>]
    VideoUrl: string option
    [<JsonPropertyName("display_url")>]
    ImageUrl: string option
    [<JsonPropertyName("is_video")>]
    IsVideo: bool
    [<JsonPropertyName("edge_media_to_caption")>]
    EdgeMediaToCaption: EdgeMediaToCaption
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

let private igReelRegex = Regex(@"(?:https?:\/\/(?:www\.)?instagram\.com\/(?:[^\/]+\/)?(?:reels?)\/)([\w-]+)", RegexOptions.Compiled)
let private igPostRegex = Regex(@"(?:https?:\/\/(?:www\.)?instagram\.com\/(?:[^\/]+\/)?(?:p?)\/)([\w-]+)", RegexOptions.Compiled)
let private mergedRegex = Regex($"{igReelRegex}|{igPostRegex}", RegexOptions.Compiled)

let private (|RegexMatch|_|) (regex: Regex) input =
    let m = regex.Match(input)
    if m.Success && m.Groups.Count > 1 then
        Some m.Groups.[1].Value
    else
        None


let private extractReelId url =
    match url with
    | RegexMatch igReelRegex id -> Reel id
    | RegexMatch igPostRegex id -> Post id
    | _ -> Nothing

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
        | Reel rId ->
            let fileName = $"ig_{rId}_{Guid.NewGuid()}.mp4"
            use mediaIdRequest = getMediaIdRequest rId
            use client = new HttpClient()
            let mediaIdResponse = executeWithPolicyAsync (client.SendAsync mediaIdRequest |> Async.AwaitTask) |> Async.RunSynchronously
            let apiResponse = mediaIdResponse.Content.ReadFromJsonAsync<InstagramMediaResponse>().Result
            apiResponse.Data
            |> Option.bind (_.InstagramXdt)
            |> Option.bind (_.VideoUrl)
            |> Option.map (fun vUrl ->
                downloadFileAsync vUrl fileName |> Async.RunSynchronously
                Reply.createVideoFile fileName)
        | Post pId ->
            let fileName = $"ig_{pId}_{Guid.NewGuid()}.mp4"
            use mediaIdRequest = getMediaIdRequest pId
            use client = new HttpClient()
            let mediaIdResponse = executeWithPolicyAsync (client.SendAsync mediaIdRequest |> Async.AwaitTask) |> Async.RunSynchronously
            let apiTextResponse = mediaIdResponse.Content.ReadAsStringAsync CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
            let apiResponse = mediaIdResponse.Content.ReadFromJsonAsync<InstagramMediaResponse>().Result
            apiResponse.Data
            |> Option.bind (_.InstagramXdt)
            |> Option.map (fun data ->
                downloadFileAsync data.ImageUrl.Value fileName |> Async.RunSynchronously
                Reply.createImageGallery [fileName] (Some data.EdgeMediaToCaption.Edges.Head.Node.Text))
            
        | Nothing -> None

let getInstagramLinks (_: string option) = getLinks mergedRegex
