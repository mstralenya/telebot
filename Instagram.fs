module Telebot.Instagram

open System
open System.IO
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Text.RegularExpressions
open System.Collections.Generic
open Telebot.InstagramData
open Telebot.Text
open Telebot.VideoDownloader

type PostType =
    | Reel of string
    | Post of string
    | Nothing

module private Constants =
    let [<Literal>] ApiEndpoint = "https://www.instagram.com/api/graphql"


module private Impl =
    let loadJson<'T> path = 
        File.ReadAllText path |> JsonSerializer.Deserialize<'T>
    
    let headers = loadJson<Dictionary<string, string>> "igHeaders.json"
    let urlContent = loadJson<KeyValuePair<string, string> list> "igUrlContent.json"
    let postRegex = Regex (@"https://www\.instagram\.com/(?:reel?|p)/([\w-]+)/?", RegexOptions.Compiled)
    let shareRegex = Regex(@"https://www\.instagram\.com/share/(?:reel/)?([a-zA-Z0-9_/-]+)/?", RegexOptions.Compiled)
    
    let (|PostType|_|) url =
        postRegex.Match(url).Groups
        |> Seq.tryLast
        |> Option.map _.Value
        |> function
            | Some id when url.Contains "/reel" -> Some (Reel id)
            | Some id -> Some (Post id)
            | _ -> None
    
    let getContentPostId postId =
        KeyValuePair("variables", $"{{\"shortcode\":\"{postId}\",\"fetch_comment_count\":null}}")
    
    let createRequest postId =
        let content = urlContent @ [getContentPostId postId]
        let request = new HttpRequestMessage(HttpMethod.Post, Constants.ApiEndpoint)
        request.Content <- new FormUrlEncodedContent(content)
        headers |> Seq.iter (fun kv -> request.Headers.Add(kv.Key, kv.Value))
        request
    
    let fetchMediaData postId = async {
        use request = createRequest postId
        let response = HttpClient.executeRequestAsync request
        return! response.Content.ReadFromJsonAsync<InstagramMediaResponse>() |> Async.AwaitTask
    }
    
    let getCaption (xdt: InstagramXdt) =
        xdt.EdgeMediaToCaption.Edges
        |> List.tryHead
        |> Option.map _.Node.Text
        |> Option.defaultValue ""

open Impl

let private getRealInstagramUrl (shareUrl: string) =
    async {
        let response = HttpClient.getAsync shareUrl
        
        if response.IsSuccessStatusCode then
            let realUrl = response.RequestMessage.RequestUri.ToString()
            return realUrl
        else
            return "Failed to retrieve the real URL."
    }

let private downloadReel rId = async {
    let! media = fetchMediaData rId
    return!
        media.Data
        |> Option.bind _.InstagramXdt
        |> Option.bind _.VideoUrl
        |> function
            | Some url ->
                let gallery = [|downloadMedia url true|] |> Async.Parallel |> Async.RunSynchronously |> List.ofArray
                async { return Reply.createGallery gallery None }
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
        
        return Reply.createGallery (List.ofArray mediaItems) (Some (getCaption xdt))
    | None ->
        return Reply.createMessage "Failed to download post"
}

let getInstagramReply url =
    async {
        match url with
        | PostType (Reel id) -> 
            let! result = downloadReel id
            return Success result
        | PostType (Post id) ->
            let! result = downloadPost id
            return Success result
        | _ -> return InvalidUrl
    }
    |> Async.RunSynchronously 
    |> function
        | Success reply -> Some reply
        | InvalidUrl -> Some (Reply.createMessage "Invalid Instagram URL")
        | DownloadError msg -> Some (Reply.createMessage msg)
    
let getInstagramShareReply url =
    let realUrl = getRealInstagramUrl url |> Async.RunSynchronously
    getInstagramReply realUrl

let getInstagramLinks (_: string option) = getLinks postRegex
let getInstagramShareLinks (_: string option) = getLinks shareRegex
