module Telebot.Instagram

open System
open System.IO
open System.Diagnostics
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions
open System.Collections.Generic
open System.Threading
open System.Xml.Linq
open System.Threading.Tasks
open Serilog
open Telebot.DataTypes
open Telebot.Bus
open Telebot.Handlers
open Telebot.InstagramData
open Telebot.Ffmpeg
open Telebot.Messages
open Telebot.PrometheusMetrics
open Telebot.Replies
open Telebot.VideoDownloader
open Wolverine.Attributes

module private Constants =
    [<Literal>]
    let ApiEndpoint = "https://www.instagram.com/api/graphql"

module Instagram =
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

    let private fetchMediaData postId useProxy =
        async {
            use request = createRequest postId
            let! response = Telebot.HttpClient.executeRequestAsync request useProxy
            let cancellationToken = CancellationToken.None
            let! body = response.Content.ReadAsStringAsync cancellationToken |> Async.AwaitTask
            let status = response.StatusCode
            response.Dispose()
            Log.Information $"fetched instagram data:\n {status} \n {body}"
            return JsonSerializer.Deserialize<InstagramMediaResponse>(body)
        }

    let private getCaption (xdt: InstagramXdt) =
        xdt.EdgeMediaToCaption.Edges
        |> List.tryHead
        |> Option.map _.Node.Text
        |> Option.defaultValue ""


    let private getRealInstagramUrl (shareUrl: string) useProxy =
        async {
            try
                let! response = Telebot.HttpClient.getAsync shareUrl useProxy
                use _ = response

                if response.IsSuccessStatusCode && response.RequestMessage.RequestUri <> null then
                    return response.RequestMessage.RequestUri.ToString()
                else
                    return shareUrl
            with _ ->
                return shareUrl
        }

    let private getBaseUrlFromDash (manifest: string) (mimeTypePrefix: string) =
        try
            let doc = XDocument.Parse(manifest)
            // 1. Try to find a Representation with matching mimeType
            let repUrl =
                doc.Descendants()
                |> Seq.tryFind (fun e -> 
                    e.Name.LocalName = "Representation" && 
                    e.Attribute(XName.Get("mimeType")) <> null && 
                    e.Attribute(XName.Get("mimeType")).Value.StartsWith(mimeTypePrefix))
                |> Option.bind (fun e -> e.Descendants() |> Seq.tryFind (fun d -> d.Name.LocalName = "BaseURL"))
                |> Option.map _.Value

            match repUrl with
            | Some url -> Some url
            | None ->
                // 2. Fallback to finding an AdaptationSet with matching mimeType or contentType
                doc.Descendants()
                |> Seq.tryFind (fun e -> 
                    e.Name.LocalName = "AdaptationSet" && (
                        (e.Attribute(XName.Get("mimeType")) <> null && e.Attribute(XName.Get("mimeType")).Value.StartsWith(mimeTypePrefix)) ||
                        (e.Attribute(XName.Get("contentType")) <> null && e.Attribute(XName.Get("contentType")).Value.StartsWith(mimeTypePrefix))
                    )
                )
                |> Option.bind (fun e -> e.Descendants() |> Seq.tryFind (fun d -> d.Name.LocalName = "BaseURL"))
                |> Option.map _.Value
        with
        | _ -> None

    // Resolves an audio stream url from a node's DASH manifest or licensed music metadata
    let private findAudioUrl (dashInfo: InstagramDashInfo option) (clipsMetadata: InstagramClipsMetadata option) =
        dashInfo
        |> Option.bind _.VideoDashManifest
        |> Option.bind (fun manifest -> getBaseUrlFromDash manifest "audio")
        |> Option.orElseWith (fun () ->
            clipsMetadata
            |> Option.bind _.MusicInfo
            |> Option.bind _.MusicAssetInfo
            |> Option.bind _.ProgressiveDownloadUrl)

    let private tryGetMediaUrlViaProxy (shortcode: string) (isVideo: bool) (useProxy: bool) : Async<string option> =
        let pattern =
            if isVideo then
                """<meta\s+property=["']og:video["']\s+content=["'](.*?)["']"""
            else
                """<meta\s+property=["']og:image["']\s+content=["'](.*?)["']"""

        let proxies = [
            "https://fxig.seria.moe"
            "https://eeinstagram.com"
            "https://instagramez.com"
        ]

        let rec tryProxies remaining =
            async {
                match remaining with
                | [] -> return None
                | proxyBase :: rest ->
                try
                    use request = new HttpRequestMessage(HttpMethod.Get, $"{proxyBase}/reel/{shortcode}/")
                    request.Headers.TryAddWithoutValidation("User-Agent", "TelegramBot (like TwitterBot)") |> ignore

                    let! response = Telebot.HttpClient.executeRequestAsync request useProxy
                    use _ = response
                    if response.IsSuccessStatusCode then
                            let! html = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                            match Regex.Match(html, pattern) with
                            | m when m.Success -> return Some m.Groups.[1].Value
                            | _ -> return! tryProxies rest
                        else
                            return! tryProxies rest
                    with ex ->
                        Log.Error(ex, $"Error requesting from proxy {proxyBase}")
                        return! tryProxies rest
            }

        tryProxies proxies

    let private tryDownloadMediaViaProxy (shortcode: string) (isVideo: bool) (useProxy: bool) : Async<GalleryDisplay option> =
        async {
            let! mediaUrlOpt = tryGetMediaUrlViaProxy shortcode isVideo useProxy
            match mediaUrlOpt with
            | Some mediaUrl ->
                let! gallery = downloadMediaAsync mediaUrl isVideo useProxy
                return Some gallery
            | None -> return None
        }

    let private downloadReel rId =
        async {
            let useProxy = Telebot.HttpClient.ProxyConfig.useProxyForInstagramReels()
            let! media = fetchMediaData rId useProxy

            match media.Data |> Option.bind _.InstagramXdt with
            | Some xdt ->
                let audioUrl = findAudioUrl xdt.DashInfo xdt.ClipsMetadata

                if audioUrl.IsSome then
                    Log.Information $"Found DASH or licensed audio stream for reel {rId}"

                match xdt.VideoUrl with
                | Some url ->
                    let! gallery = [| downloadMediaWithAudioAsync url audioUrl true useProxy |] |> Async.Parallel
                    return Reply.createGallery gallery None
                | None -> return Reply.createMessage "Failed to download reel"
            | None ->
                Log.Information $"GraphQL query failed for reel {rId}. Trying proxy fallback..."
                let! proxyMedia = tryDownloadMediaViaProxy rId true useProxy
                match proxyMedia with
                | Some gallery -> return Reply.createGallery [| gallery |] None
                | None -> return Reply.createMessage "Failed to download reel"
        }

    let private downloadPost pId =
        async {
            let useProxy = Telebot.HttpClient.ProxyConfig.useProxyForInstagramPosts()
            let! media = fetchMediaData pId useProxy

            match media.Data |> Option.bind _.InstagramXdt with
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
                            
                            let audioUrl =
                                if e.Node.IsVideo then
                                    let url = findAudioUrl e.Node.DashInfo e.Node.ClipsMetadata
                                    if url.IsSome then Log.Information $"Found DASH or licensed audio stream for sidecar item in post {pId}"
                                    url
                                else None

                            downloadMediaWithAudioAsync downloadUrl audioUrl e.Node.IsVideo useProxy)
                        |> Async.Parallel
                    | _ ->
                        let url = if xdt.IsVideo then xdt.VideoUrl else xdt.ImageUrl
                        
                        let audioUrl =
                            if xdt.IsVideo then
                                let aUrl = findAudioUrl xdt.DashInfo xdt.ClipsMetadata
                                if aUrl.IsSome then Log.Information $"Found DASH or licensed audio stream for post {pId}"
                                aUrl
                            else None

                        match url with
                        | Some u -> [| downloadMediaWithAudioAsync u audioUrl xdt.IsVideo useProxy |]
                        | None -> [||]
                        |> Async.Parallel

                return Reply.createGallery mediaItems (Some(getCaption xdt))
            | None ->
                Log.Information $"GraphQL query failed for post {pId}. Trying proxy fallback..."
                let! proxyVideo = tryDownloadMediaViaProxy pId true useProxy
                match proxyVideo with
                | Some gallery -> return Reply.createGallery [| gallery |] None
                | None ->
                    let! proxyPhoto = tryDownloadMediaViaProxy pId false useProxy
                    match proxyPhoto with
                    | Some gallery -> return Reply.createGallery [| gallery |] None
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

    let getInstagramShareReplyAsync (url: string) =
        async {
            let useProxy =
                if url.Contains("/reel") then Telebot.HttpClient.ProxyConfig.useProxyForInstagramReels()
                else Telebot.HttpClient.ProxyConfig.useProxyForInstagramPosts()
            let! realUrl = getRealInstagramUrl url useProxy
            return! getInstagramReply realUrl
        }

    // Audio extraction: download video then extract audio via ffmpeg
    let private extractAudioFromVideoAsync (videoUrl: string) (id: string option) useProxy =
        async {
            try
                let name = Guid.NewGuid().ToString("N")
                let defaultId = defaultArg id "noid"
                let outVideo = $"ig_{defaultId}_{name}.mp4"
                do! downloadFileAsync videoUrl outVideo useProxy
                let outAudio = Path.ChangeExtension(outVideo, ".mp3")

                let args = sprintf "-y -i \"%s\" -vn -acodec libmp3lame -q:a 2 \"%s\"" outVideo outAudio

                match! runProcessCaptureAsync "ffmpeg" args ffmpegTimeoutMs with
                | Error msg ->
                    do! deleteFileAsync outVideo |> Async.Catch |> Async.Ignore
                    return Choice2Of2 msg
                | Ok (exitCode, _, _) ->
                    if exitCode = 0 && File.Exists outAudio then
                        do! deleteFileAsync outVideo |> Async.Catch |> Async.Ignore
                        return Choice1Of2 outAudio
                    else
                        do! deleteFileAsync outVideo |> Async.Catch |> Async.Ignore
                        do! deleteFileAsync outAudio |> Async.Catch |> Async.Ignore
                        return Choice2Of2 "ffmpeg failed to extract audio"
            with ex ->
                return Choice2Of2 ex.Message
        }

    let getInstagramAudioReplyAsync url =
        async {
            try
                match url with
                | PostType(Reel id) ->
                    let useProxy = Telebot.HttpClient.ProxyConfig.useProxyForInstagramReels()
                    let! media = fetchMediaData id useProxy
                    let! videoUrlOpt =
                        async {
                            match media.Data |> Option.bind _.InstagramXdt |> Option.bind _.VideoUrl with
                            | Some vurl -> return Some vurl
                            | None -> return! tryGetMediaUrlViaProxy id true useProxy
                        }
                    match videoUrlOpt with
                    | Some vurl ->
                        let! res = extractAudioFromVideoAsync vurl (Some id) useProxy
                        match res with
                        | Choice1Of2 audioPath -> return Some (Reply.createAudioFile audioPath)
                        | Choice2Of2 msg -> return Some (Reply.createMessage msg)
                    | None -> return Some (Reply.createMessage "Failed to find video for reel")
                | PostType(Post id) ->
                    let useProxy = Telebot.HttpClient.ProxyConfig.useProxyForInstagramPosts()
                    let! media = fetchMediaData id useProxy
                    let! videoUrlOpt =
                        async {
                            match media.Data |> Option.bind _.InstagramXdt with
                            | Some xdt when xdt.IsVideo -> return xdt.VideoUrl
                            | _ -> return! tryGetMediaUrlViaProxy id true useProxy
                        }
                    match videoUrlOpt with
                    | Some vurl ->
                        let! res = extractAudioFromVideoAsync vurl (Some id) useProxy
                        match res with
                        | Choice1Of2 audioPath -> return Some (Reply.createAudioFile audioPath)
                        | Choice2Of2 msg -> return Some (Reply.createMessage msg)
                    | None -> return Some (Reply.createMessage "No video to extract audio from")
                | _ -> return Some (Reply.createMessage "Invalid Instagram URL for audio extraction")
            with ex -> return Some (Reply.createMessage ex.Message)
        }


type InstagramLinksHandler() =
    inherit BaseHandler()
    member private this.getInstagramShareLinks (message: string option) = getLinks Instagram.shareRegex message
    member private this.getInstagramLinks (message: string option) = getLinks Instagram.postRegex message
    member private this.getInstagramAudioLinks (message: string option) =
        match message with
        | Some text when text.IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0 -> getLinks Instagram.postRegex message
        | _ -> List.empty
    member private this.getInstagramVideoLinks (message: string option) =
        match message with
        | Some text when text.IndexOf("audio", StringComparison.OrdinalIgnoreCase) < 0 -> getLinks Instagram.postRegex message
        | _ -> List.empty
    member private this.extractInstagramShareLinks =
        createLinkExtractor this.getInstagramShareLinks InstagramShareMessage
    member private this.extractInstagramLinks =
        createLinkExtractor this.getInstagramLinks InstagramMessage
    member private this.extractInstagramAudioLinks =
        createLinkExtractor this.getInstagramAudioLinks InstagramAudioMessage
    member private this.extractInstagramVideoLinks =
        createLinkExtractor this.getInstagramVideoLinks InstagramMessage
    [<WolverineHandler>]
    member this.HandleLinks(msg: UpdateMessage) : Task =
        let links = this.extractInstagramVideoLinks msg
        task {
            for message in links do
                do! publishToBusAsync message |> Async.StartAsTask
        }
    [<WolverineHandler>]
    member this.HandleShareLinks(msg: UpdateMessage) : Task =
        let links = this.extractInstagramShareLinks msg
        task {
            for message in links do
                do! publishToBusAsync message |> Async.StartAsTask
        }
    [<WolverineHandler>]
    member this.HandleAudioLinks(msg: UpdateMessage) : Task =
        let links = this.extractInstagramAudioLinks msg
        task {
            for message in links do
                do! publishToBusAsync message |> Async.StartAsTask
        }
    member this.Handle(msg: InstagramMessage) =
        this.processLinkAsync msg Instagram.getInstagramReply
    member this.Handle(msg: InstagramShareMessage) =
        this.processLinkAsync msg Instagram.getInstagramShareReplyAsync
    member this.Handle(msg: InstagramAudioMessage) =
        this.processLinkAsync msg Instagram.getInstagramAudioReplyAsync
    
