module Telebot.Instagram

open System
open System.IO
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions
open System.Collections.Generic
open Telebot.Policies
open Telebot.Text
open Telebot.VideoDownloader

type PostType =
    | Reel of string
    | Post of string
    | Nothing

[<Struct>]
type Dimension = { Height: int; Width: int }

type SharingFrictionInfo = {
    [<JsonPropertyName("should_have_sharing_friction")>]
    ShouldHaveSharingFriction: bool
    [<JsonPropertyName("bloks_app_url")>]
    BloksAppUrl: string option
}

type DisplayResource = {
    [<JsonPropertyName("src")>]
    SourceUrl: string
    [<JsonPropertyName("config_width")>]
    ConfigWidth: int
    [<JsonPropertyName("config_height")>]
    ConfigHeight: int
}

type TaggedUserEdge = {
    [<JsonPropertyName("edges")>]
    Edges: obj list
}

type MediaNode = {
    [<JsonPropertyName("__typename")>]
    TypeName: string
    [<JsonPropertyName("id")>]
    Id: string
    [<JsonPropertyName("shortcode")>]
    Shortcode: string
    [<JsonPropertyName("dimensions")>]
    Dimensions: Dimension
    [<JsonPropertyName("gating_info")>]
    GatingInfo: obj option
    [<JsonPropertyName("fact_check_overall_rating")>]
    FactCheckOverallRating: obj option
    [<JsonPropertyName("fact_check_information")>]
    FactCheckInformation: obj option
    [<JsonPropertyName("sensitivity_friction_info")>]
    SensitivityFrictionInfo: obj option
    [<JsonPropertyName("sharing_friction_info")>]
    SharingFrictionInfo: SharingFrictionInfo
    [<JsonPropertyName("media_overlay_info")>]
    MediaOverlayInfo: obj option
    [<JsonPropertyName("media_preview")>]
    MediaPreview: string option
    [<JsonPropertyName("display_url")>]
    DisplayUrl: string
    [<JsonPropertyName("display_resources")>]
    DisplayResources: DisplayResource list
    [<JsonPropertyName("accessibility_caption")>]
    AccessibilityCaption: string option
    [<JsonPropertyName("is_video")>]
    IsVideo: bool
    [<JsonPropertyName("tracking_token")>]
    TrackingToken: string
    [<JsonPropertyName("upcoming_event")>]
    UpcomingEvent: obj option
    [<JsonPropertyName("edge_media_to_tagged_user")>]
    EdgeMediaToTaggedUser: TaggedUserEdge
}

type Edge = {
    [<JsonPropertyName("node")>]
    Node: MediaNode
}

type EdgeSidecarToChildren = {
    [<JsonPropertyName("edges")>]
    Edges: Edge list
}

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

type InstagramXdt = {
    [<JsonPropertyName("video_url")>]
    VideoUrl: string option
    [<JsonPropertyName("display_url")>]
    ImageUrl: string option
    [<JsonPropertyName("is_video")>]
    IsVideo: bool
    [<JsonPropertyName("edge_media_to_caption")>]
    EdgeMediaToCaption: {| Edges: CaptionEdge list |}
    [<JsonPropertyName("edge_sidecar_to_children")>]
    EdgeSidecarToChildren: EdgeSidecarToChildren option
}

type Data = {
    [<JsonPropertyName("xdt_shortcode_media")>]
    InstagramXdt: InstagramXdt option
}

type InstagramMediaResponse = {
    [<JsonPropertyName("data")>]
    Data: Data option
}

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
        |> Option.map (fun g -> g.Value)
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
        return! response.Content.ReadFromJsonAsync<InstagramMediaResponse>() |> Async.AwaitTask
    }
    
    let downloadMedia url fileName = async {
        do! downloadFileAsync url fileName
        return if fileName.Contains("igv_") then Video fileName else Photo fileName
    }
    
    let getCaption (xdt: InstagramXdt) =
        xdt.EdgeMediaToCaption.Edges
        |> List.tryHead
        |> Option.map (fun e -> e.Node.Text)
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
                downloadMedia url fileName
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
                |> List.map (fun e -> downloadMedia e.Node.DisplayUrl $"ig_{Guid.NewGuid()}.mp4")
                |> Async.Parallel
            | _ ->
                let url = if xdt.IsVideo then xdt.VideoUrl else xdt.ImageUrl
                match url with
                | Some u -> [| downloadMedia u $"""ig_{Guid.NewGuid()}.{(if xdt.IsVideo then "mp4" else "jpg")}""" |]
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