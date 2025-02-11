module Telebot.Instagram

open System
open System.IO
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Text.RegularExpressions
open System.Collections.Generic
open System.Threading
open Telebot.InstagramData
open Telebot.Policies
open Telebot.Text
open Telebot.VideoDownloader

type PostType =
    | Reel of string
    | Post of string
    | Nothing

module Async =
    let map f computation =
        async.Bind(computation, f >> async.Return)

module private Impl =
    let loadJson<'T> path = 
        File.ReadAllText path |> JsonSerializer.Deserialize<'T>
    
    let headers = loadJson<Dictionary<string, string>> "igHeaders.json"
    let urlContent = loadJson<KeyValuePair<string, string> list> "igUrlContent.json"
    let postRegex = Regex (@"/(?:reels?|p)/([\w-]+)/?", RegexOptions.Compiled)
    
    let (|PostType|_|) url =
        postRegex.Match(url).Groups
        |> Seq.tryLast
        |> Option.map (_.Value)
        |> function
            | Some id when url.Contains("/reel") -> Some (Reel id)
            | Some id -> Some (Post id)
            | _ -> None
    
    let getContentPostId postId =
        KeyValuePair("variables", $"{{\"shortcode\":\"{postId}\",\"fetch_comment_count\":null}}")
    
    let createRequest postId =
        let content = urlContent @ [getContentPostId postId]
        let request = new HttpRequestMessage(HttpMethod.Post, "https://www.instagram.com/api/graphql")
        request.Content <- new FormUrlEncodedContent(content)
        headers |> Seq.iter (fun kv -> request.Headers.Add(kv.Key, kv.Value))
        request
    
    let fetchMediaData postId = async {
        use client = new HttpClient()
        use request = createRequest postId
        let! response = executeWithPolicyAsync (client.SendAsync request |> Async.AwaitTask)
        let r = response.Content.ReadAsStringAsync CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
        return! response.Content.ReadFromJsonAsync<InstagramMediaResponse>() |> Async.AwaitTask
    }
    
    let downloadMedia url isVideo = async {
        let fileName = $"""ig_{Guid.NewGuid()}.{(if isVideo then "mp4" else "jpg")}"""
        do! downloadFileAsync url fileName
        return if isVideo then Video fileName else Photo fileName
    }
    
    let getCaption (xdt: InstagramXdt) =
        xdt.EdgeMediaToCaption.Edges
        |> List.tryHead
        |> Option.map (_.Node.Text)
        |> Option.defaultValue ""

open Impl

let private downloadReel rId = async {
    let fileName = $"igv_{Guid.NewGuid()}.mp4"
    let! media = fetchMediaData rId
    return!
        media.Data
        |> Option.bind (_.InstagramXdt)
        |> Option.bind (_.VideoUrl)
        |> function
            | Some url -> 
                downloadMedia url true
                |> Async.map (Reply.createVideoFile << fun _ -> fileName)
            | None -> async { return Reply.createMessage "Failed to download reel" }
}

let private downloadPost pId = async {
    let! media = fetchMediaData pId
    match media.Data |> Option.bind (fun d -> d.InstagramXdt) with
    | Some xdt ->
        let! mediaItems =
            match xdt.EdgeSidecarToChildren with
            | Some { Edges = edges } when not edges.IsEmpty ->
                edges
                |> List.map (fun e ->
                    let downloadUrl = if e.Node.IsVideo then e.Node.VideoUrl else e.Node.DisplayUrl
                    downloadMedia downloadUrl e.Node.IsVideo )
                |> Async.Parallel
            | _ ->
                let url = if xdt.IsVideo then xdt.VideoUrl else xdt.ImageUrl
                match url with
                | Some u -> [| downloadMedia u xdt.IsVideo |]
                | None -> [||]
                |> Async.Parallel
        
        return Reply.createImageGallery (List.ofArray mediaItems) (Some (getCaption xdt))
    | None ->
        return Reply.createMessage "Failed to download post"
}

let getInstagramReply url =
    async {
        match url with
        | PostType (Reel id) -> return! downloadReel id
        | PostType (Post id) -> return! downloadPost id
        | _ -> return Reply.createMessage "Invalid Instagram URL"
    }
    |> Async.RunSynchronously |> Some

let getInstagramLinks (_: string option) = getLinks postRegex