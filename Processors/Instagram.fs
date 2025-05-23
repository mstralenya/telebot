module Telebot.Instagram

open System.IO
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Text.RegularExpressions
open System.Collections.Generic
open System.Threading
open Serilog
open Telebot.DataTypes
open Telebot.Bus
open Telebot.Handlers
open Telebot.InstagramData
open Telebot.Messages
open Telebot.PrometheusMetrics
open Telebot.Text
open Telebot.VideoDownloader
open Wolverine.Attributes

module private Constants =
    [<Literal>]
    let ApiEndpoint = "https://www.instagram.com/api/graphql"

module private Instagram =
    type private InstagramPostType =
        | Reel of string
        | Post of string
        | Nothing

    let private loadJson<'T> path =
        File.ReadAllText path |> JsonSerializer.Deserialize<'T>

    let private headers = loadJson<Dictionary<string, string>> "igHeaders.json"
    let private urlContent = loadJson<KeyValuePair<string, string> list> "igUrlContent.json"

    let postRegex =
        Regex(@"https://www\.instagram\.com/(?:reel?|p)/([\w-]+)/?", RegexOptions.Compiled)

    let shareRegex =
        Regex(@"https://www\.instagram\.com/share/(?:reel/)?([a-zA-Z0-9_/-]+)/?", RegexOptions.Compiled)

    let private (|PostType|_|) url =
        postRegex.Match(url).Groups
        |> Seq.tryLast
        |> Option.map _.Value
        |> function
            | Some id when url.Contains "/reel" -> id |> Reel |> Some
            | Some id -> id |> Post |> Some
            | _ -> None

    let private getContentPostId postId =
        KeyValuePair("variables", JsonSerializer.Serialize {| shortcode = postId; |})

    let private createRequest postId =
        let content = [ getContentPostId postId ] @ urlContent 
        let request = new HttpRequestMessage(HttpMethod.Post, Constants.ApiEndpoint)
        request.Content <- new FormUrlEncodedContent(content)
        headers |> Seq.iter (fun kv -> request.Headers.Add(kv.Key, kv.Value))
        Log.Information $"created instagram content {JsonSerializer.Serialize content}"
        Log.Information $"created instagram request {JsonSerializer.Serialize request}"
        request

    let private fetchMediaData postId =
        async {
            use request = createRequest postId
            let! response = Telebot.HttpClient.executeRequestAsync request
            let cancellationToken = CancellationToken.None
            Log.Information $"fetched instagram data:\n {response.StatusCode} \n {response.Content.ReadAsStringAsync cancellationToken}"
            return! response.Content.ReadFromJsonAsync<InstagramMediaResponse>() |> Async.AwaitTask
        }

    let private getCaption (xdt: InstagramXdt) =
        xdt.EdgeMediaToCaption.Edges
        |> List.tryHead
        |> Option.map _.Node.Text
        |> Option.defaultValue ""


    let private getRealInstagramUrl (shareUrl: string) =
        async {
            let! response = Telebot.HttpClient.getAsync shareUrl

            if response.IsSuccessStatusCode then
                let realUrl = response.RequestMessage.RequestUri.ToString()
                return realUrl
            else
                return "Failed to retrieve the real URL."
        }

    let private downloadReel rId =
        async {
            let! media = fetchMediaData rId

            return!
                media.Data
                |> Option.bind _.InstagramXdt
                |> Option.bind _.VideoUrl
                |> function
                    | Some url ->
                        async {
                            let! gallery = [| downloadMediaAsync url true |] |> Async.Parallel
                            return Reply.createGallery gallery None
                        }
                    | None -> async { return Reply.createMessage "Failed to download reel" }
        }

    let private downloadPost pId =
        async {
            let! media = fetchMediaData pId

            match media.Data |> Option.bind (fun d -> d.InstagramXdt) with
            | Some xdt ->
                let! mediaItems =
                    match xdt.EdgeSidecarToChildren with
                    | Some { Edges = edges } when not edges.IsEmpty ->
                        edges
                        |> List.map (fun e ->
                            let downloadUrl =
                                if e.Node.IsVideo then
                                    e.Node.VideoUrl
                                else
                                    e.Node.DisplayUrl

                            downloadMediaAsync downloadUrl e.Node.IsVideo)
                        |> Async.Parallel
                    | _ ->
                        let url = if xdt.IsVideo then xdt.VideoUrl else xdt.ImageUrl

                        match url with
                        | Some u -> [| downloadMediaAsync u xdt.IsVideo |]
                        | None -> [||]
                        |> Async.Parallel

                return Reply.createGallery mediaItems (Some(getCaption xdt))
            | None -> return Reply.createMessage "Failed to download post"
        }

    let getInstagramReplyAsync url =
        async {
            try
                match url with
                | PostType(Reel id) ->
                    let! res = downloadReel id
                    return Success res
                | PostType(Post id) ->
                    let! res = downloadPost id
                    return Success res
                | _ -> return InvalidUrl
            with
            | ex ->
                Log.Error(ex, "Error processing Instagram URL: {Url}", url)
                return DownloadError ex.Message
        }

    let getInstagramReply url =
        async {
            let! result = getInstagramReplyAsync url
            match result with
            | Success reply ->
                instagramSuccessCounter.Inc()
                return Some reply
            | InvalidUrl ->
                instagramMissingVideoIdCounter.Inc()
                return Some(Reply.createMessage "Invalid Instagram URL")
            | DownloadError msg ->
                instagramFailureCounter.Inc()
                return Some(Reply.createMessage msg)
        }

    let getInstagramReplySync url =
        getInstagramReply url |> Async.RunSynchronously

    let getInstagramShareReplyAsync url =
        async {
            let! realUrl = getRealInstagramUrl url
            return! getInstagramReply realUrl
        }

    let getInstagramShareReply url =
        getInstagramShareReplyAsync url |> Async.RunSynchronously

    let getInstagramShareReplySync url =
        getInstagramShareReply url


type InstagramLinksHandler() =
    inherit BaseHandler()
    member private this.getInstagramShareLinks (message: string option) = getLinks Instagram.shareRegex message
    member private this.getInstagramLinks (message: string option) = getLinks Instagram.postRegex message
    member private this.extractInstagramShareLinks =
        createLinkExtractor this.getInstagramShareLinks InstagramShareMessage
    member private this.extractInstagramLinks =
        createLinkExtractor this.getInstagramLinks InstagramMessage
    [<WolverineHandler>]
    member this.HandleLinks(msg: UpdateMessage) =
        this.extractInstagramLinks msg |> List.map (publishToBusAsync >> Async.RunSynchronously) |> ignore
    [<WolverineHandler>]
    member this.HandleShareLinks(msg: UpdateMessage) =
        this.extractInstagramShareLinks msg |> List.map (publishToBusAsync >> Async.RunSynchronously) |> ignore
    member this.Handle(msg: InstagramMessage) =
        this.processLink msg Instagram.getInstagramReplySync
    member this.Handle(msg: InstagramShareMessage) =
        this.processLink msg Instagram.getInstagramShareReplySync
    
