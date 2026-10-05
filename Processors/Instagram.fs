module Telebot.Instagram

open System
open System.IO
open System.Diagnostics
open System.Net.Http
open System.Net
open System.Text.Json
open System.Text.Json.Nodes
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

    [<Literal>]
    let PostDocId = "27130156389949648"

    [<Literal>]
    let PostFriendlyName = "PolarisLoggedOutDesktopWWWPostRootContentQuery"

module Instagram =
    type private InstagramPostType =
        | Reel of string
        | Post of string * mediaIndex: int option
        | Story of username: string * mediaId: string
        | Nothing

    let private loadJson<'T> path =
        File.ReadAllText path |> JsonSerializer.Deserialize<'T>

    let private headers = loadJson<Dictionary<string, string>> "igHeaders.json"
    let private urlContent = loadJson<KeyValuePair<string, string> list> "igUrlContent.json"

    let postRegex =
        Regex(@"https://www\.instagram\.com/(?:reel?|p)/([\w-]+)/?(?:\?[^\s]*)?", RegexOptions.Compiled)

    let shareRegex =
        Regex(@"https://www\.instagram\.com/share/(?:reel/)?([a-zA-Z0-9_/-]+)/?", RegexOptions.Compiled)

    let storyRegex =
        Regex(
            @"https://(?:www\.)?instagram\.com/stories/(?!highlights(?:/|$))([a-zA-Z0-9._]+)/([0-9]+)/?(?:\?[^\s]*)?",
            RegexOptions.Compiled ||| RegexOptions.IgnoreCase
        )

    let internal mediaIdToShortcode (mediaId: string) =
        let alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
        match UInt64.TryParse mediaId with
        | true, 0UL -> Some "A"
        | true, value ->
            let mutable remaining = value
            let mutable shortcode = ""
            while remaining > 0UL do
                shortcode <- string alphabet.[int (remaining % 64UL)] + shortcode
                remaining <- remaining / 64UL
            Some shortcode
        | _ -> None

    let internal shortcodeToMediaId (shortcode: string) =
        let alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
        if String.IsNullOrWhiteSpace shortcode then None
        else
            shortcode
            |> Seq.fold (fun state character ->
                state
                |> Option.bind (fun value ->
                    let digit = alphabet.IndexOf character
                    if digit < 0 || value > (UInt64.MaxValue - uint64 digit) / 64UL then None
                    else Some(value * 64UL + uint64 digit))) (Some 0UL)
            |> Option.map string

    let internal tryGetMediaIndex (url: string) =
        let m = Regex.Match(url, @"(?:\?|&)img_index=([0-9]+)(?:&|$)", RegexOptions.IgnoreCase)
        match m.Success, Int32.TryParse m.Groups.[1].Value with
        | true, (true, index) when index > 0 -> Some index
        | _ -> None

    let private (|PostType|_|) url =
        let storyMatch = storyRegex.Match url
        if storyMatch.Success then
            Some(Story(storyMatch.Groups.[1].Value, storyMatch.Groups.[2].Value))
        else
            postRegex.Match(url).Groups
            |> Seq.tryLast
            |> Option.map _.Value
            |> function
                | Some id when url.Contains "/reel" -> id |> Reel |> Some
                | Some id -> Post(id, tryGetMediaIndex url) |> Some
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

    let internal tryParseInstagramMediaResponse (body: string) =
        if String.IsNullOrWhiteSpace body then
            None
        else
            try
                JsonSerializer.Deserialize<InstagramMediaResponse>(body)
                |> Option.ofObj
            with ex ->
                Log.Warning(ex, "Could not parse legacy Instagram media response")
                None

    let private emptyMediaResponse : InstagramMediaResponse = { Data = None }

    let private fetchMediaData postId useProxy =
        async {
            use request = createRequest postId
            let! response = Telebot.HttpClient.executeRequestAsync request useProxy
            use _ = response
            let cancellationToken = CancellationToken.None
            let! body = response.Content.ReadAsStringAsync cancellationToken |> Async.AwaitTask
            Log.Information(
                "Fetched legacy Instagram data for {Shortcode}: {StatusCode}, {BodyLength} bytes",
                postId,
                response.StatusCode,
                body.Length
            )

            if not response.IsSuccessStatusCode then
                Log.Warning(
                    "Legacy Instagram query failed for {Shortcode}: {StatusCode}; trying fallback",
                    postId,
                    response.StatusCode
                )
                return emptyMediaResponse
            else
                match tryParseInstagramMediaResponse body with
                | Some media -> return media
                | None -> return emptyMediaResponse
        }

    let private tryProperty (name: string) (node: JsonNode) =
        if isNull node then None else node[name] |> Option.ofObj

    let private tryString name node =
        tryProperty name node |> Option.map _.GetValue<string>()

    let private tryFirstArrayItem name node =
        tryProperty name node
        |> Option.bind (fun array -> array.AsArray() |> Seq.tryHead)

    let private tryModernMediaItem (node: JsonNode) =
        let mediaType =
            tryProperty "media_type" node
            |> Option.map _.GetValue<int>()
            |> Option.defaultValue 1

        if mediaType = 2 then
            tryFirstArrayItem "video_versions" node
            |> Option.bind (tryString "url")
            |> Option.map (fun url -> url, true)
        else
            tryProperty "image_versions2" node
            |> Option.bind (tryFirstArrayItem "candidates")
            |> Option.bind (tryString "url")
            |> Option.map (fun url -> url, false)

    let internal parseModernInstagramMedia (body: string) =
        try
            let root = JsonNode.Parse body
            let product =
                root
                |> tryProperty "data"
                |> Option.bind (tryProperty "xig_polaris_media")
                |> Option.bind (tryProperty "if_not_gated_logged_out")

            product
            |> Option.bind (fun media ->
                let items =
                    match tryProperty "carousel_media" media with
                    | Some carousel -> carousel.AsArray() |> Seq.choose tryModernMediaItem |> Seq.toList
                    | None -> tryModernMediaItem media |> Option.toList

                if List.isEmpty items then None
                else
                    let caption =
                        tryProperty "caption" media
                        |> Option.bind (tryString "text")
                    Some(items, caption))
        with ex ->
            Log.Warning(ex, "Could not parse modern Instagram media response")
            None

    let private createModernPostRequest shortcode =
        let mediaId = shortcodeToMediaId shortcode |> Option.defaultValue shortcode
        let lsd =
            urlContent
            |> List.tryFind (fun item -> item.Key = "lsd")
            |> Option.map _.Value
            |> Option.defaultValue ""
        let content =
            [ KeyValuePair("lsd", lsd)
              KeyValuePair("fb_api_caller_class", "RelayModern")
              KeyValuePair("fb_api_req_friendly_name", Constants.PostFriendlyName)
              KeyValuePair("server_timestamps", "true")
              KeyValuePair("variables", JsonSerializer.Serialize {| media_id = mediaId |})
              KeyValuePair("doc_id", Constants.PostDocId) ]
        let request = new HttpRequestMessage(HttpMethod.Post, Constants.ApiEndpoint)
        request.Content <- new FormUrlEncodedContent(content)
        headers
        |> Seq.filter (fun item -> not (item.Key.Equals("X-FB-Friendly-Name", StringComparison.OrdinalIgnoreCase)))
        |> Seq.iter (fun item -> request.Headers.TryAddWithoutValidation(item.Key, item.Value) |> ignore)
        request.Headers.TryAddWithoutValidation("X-FB-Friendly-Name", Constants.PostFriendlyName) |> ignore
        request

    let private fetchModernPostMedia shortcode useProxy =
        async {
            try
                use request = createModernPostRequest shortcode
                let! response = Telebot.HttpClient.executeRequestAsync request useProxy
                use _ = response
                let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                Log.Information("Fetched current Instagram post data for {Shortcode}: {StatusCode}", shortcode, response.StatusCode)
                return parseModernInstagramMedia body
            with ex ->
                Log.Warning(ex, "Current Instagram post query failed for {Shortcode}", shortcode)
                return None
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

    let internal tryGetOpenGraphMedia (html: string) =
        let tryProperty propertyName =
            let pattern = $"""<meta\s+property=["']{propertyName}["']\s+content=["'](.*?)["']"""
            match Regex.Match(html, pattern, RegexOptions.IgnoreCase) with
            | m when m.Success -> Some(WebUtility.HtmlDecode m.Groups.[1].Value)
            | _ -> None

        match tryProperty "og:video" with
        | Some url -> Some(url, true)
        | None -> tryProperty "og:image" |> Option.map (fun url -> url, false)

    let internal resolveMediaUrl (proxyBase: string) (mediaUrl: string) =
        match Uri.TryCreate(mediaUrl, UriKind.Absolute) with
        | true, absolute when absolute.Scheme = Uri.UriSchemeHttp || absolute.Scheme = Uri.UriSchemeHttps ->
            absolute.ToString()
        | _ -> Uri(Uri(proxyBase.TrimEnd('/') + "/"), mediaUrl).ToString()

    let internal classifyMediaContentType (contentType: string option) =
        contentType
        |> Option.bind (fun value ->
            if value.StartsWith("video/", StringComparison.OrdinalIgnoreCase) then Some true
            elif value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) then Some false
            else None)

    let internal matchesExpectedMediaType (expectedIsVideo: bool option) (actualIsVideo: bool) =
        expectedIsVideo |> Option.forall ((=) actualIsVideo)

    let private tryGetMediaViaProxyPath
        (path: string)
        (expectedIsVideo: bool option)
        (useProxy: bool)
        : Async<(string * bool) option> =

        let proxies = [
            "https://eeinstagram.com"
            "https://kkinstagram.com"
            "https://fxig.seria.moe"
        ]

        let rec tryProxies remaining =
            async {
                match remaining with
                | [] -> return None
                | proxyBase :: rest ->
                try
                    use request = new HttpRequestMessage(HttpMethod.Get, $"{proxyBase}{path}")
                    request.Headers.TryAddWithoutValidation("User-Agent", "TelegramBot (like TwitterBot)") |> ignore

                    let! response = Telebot.HttpClient.executeRequestAsync request useProxy
                    use _ = response
                    if response.IsSuccessStatusCode then
                        let contentType =
                            response.Content.Headers.ContentType
                            |> Option.ofObj
                            |> Option.bind (fun header -> header.MediaType |> Option.ofObj)

                        match classifyMediaContentType contentType with
                        | Some isVideo when matchesExpectedMediaType expectedIsVideo isVideo ->
                            let mediaUrl =
                                response.RequestMessage
                                |> Option.ofObj
                                |> Option.bind (fun message -> message.RequestUri |> Option.ofObj)
                                |> Option.map string
                                |> Option.defaultValue $"{proxyBase}{path}"
                            return Some(mediaUrl, isVideo)
                        | Some _ -> return! tryProxies rest
                        | None ->
                            let! html = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                            match tryGetOpenGraphMedia html with
                            | Some (url, isVideo) when matchesExpectedMediaType expectedIsVideo isVideo ->
                                return Some(resolveMediaUrl proxyBase url, isVideo)
                            | _ -> return! tryProxies rest
                    else
                        return! tryProxies rest
                    with ex ->
                        Log.Error(ex, $"Error requesting from proxy {proxyBase}")
                        return! tryProxies rest
            }

        tryProxies proxies

    let private tryGetMediaUrlViaProxyPath (path: string) (isVideo: bool) (useProxy: bool) : Async<string option> =
        async {
            match! tryGetMediaViaProxyPath path (Some isVideo) useProxy with
            | Some (url, _) -> return Some url
            | _ -> return None
        }

    let private tryGetMediaUrlViaProxy (shortcode: string) (isVideo: bool) (useProxy: bool) =
        tryGetMediaUrlViaProxyPath $"/reel/{shortcode}/" isVideo useProxy

    let private tryDownloadMediaViaProxyPath (path: string) (isVideo: bool) (useProxy: bool) : Async<GalleryDisplay option> =
        async {
            let! mediaUrlOpt = tryGetMediaUrlViaProxyPath path isVideo useProxy
            match mediaUrlOpt with
            | Some mediaUrl ->
                let! gallery = downloadMediaAsync mediaUrl isVideo useProxy
                return Some gallery
            | None -> return None
        }

    let private tryDownloadMediaViaProxy (shortcode: string) (isVideo: bool) (useProxy: bool) : Async<GalleryDisplay option> =
        tryDownloadMediaViaProxyPath $"/reel/{shortcode}/" isVideo useProxy

    let private tryDownloadCarouselViaProxy (shortcode: string) (useProxy: bool) : Async<GalleryDisplay array> =
        let rec resolveItems index seen resolved =
            async {
                if index > 20 then
                    return List.rev resolved
                else
                    let path = $"/p/{shortcode}/?img_index={index}"
                    match! tryGetMediaViaProxyPath path None useProxy with
                    | Some (url, isVideo) when not (Set.contains url seen) ->
                        return! resolveItems (index + 1) (Set.add url seen) ((url, isVideo) :: resolved)
                    | _ -> return List.rev resolved
            }

        async {
            let! resolved = resolveItems 1 Set.empty []
            if List.isEmpty resolved then
                return [||]
            else
                Log.Information("Resolved {Count} indexed media items for Instagram carousel {Shortcode}", resolved.Length, shortcode)
                return!
                    resolved
                    |> List.map (fun (url, isVideo) -> downloadMediaAsync url isVideo useProxy)
                    |> Async.Parallel
        }

    let private tryDownloadStoryWithYtDlp (storyUrl: string) (useProxy: bool) : Async<GalleryDisplay option> =
        async {
            match Config.get().InstagramCookiesPath with
            | None -> return None
            | Some cookiesPath ->
                let outputBase = $"ig_story_{Guid.NewGuid():N}"
                let outputTemplate = $"{outputBase}.%%(ext)s"
                let proxyArg =
                    if useProxy then
                        Telebot.HttpClient.ProxyConfig.getProxyUrl()
                        |> Option.map (fun proxy -> $" --proxy \"{proxy}\"")
                        |> Option.defaultValue ""
                    else ""
                let args =
                    $"--no-playlist --no-warnings --cookies \"{cookiesPath}\"{proxyArg} --print after_move:filepath -o \"{outputTemplate}\" \"{storyUrl}\""

                match! runProcessCaptureAsync "yt-dlp" args 120_000 with
                | Ok (0, stdout, _) ->
                    let outputPath =
                        stdout.Split([|'\r'; '\n'|], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.tryLast
                    match outputPath with
                    | Some path when File.Exists path ->
                        let extension = Path.GetExtension(path).ToLowerInvariant()
                        let isVideo = [ ".mp4"; ".webm"; ".mkv"; ".mov" ] |> List.contains extension
                        return Some(if isVideo then Video path else Photo path)
                    | _ -> return None
                | Ok (_, _, stderr) ->
                    Log.Warning("yt-dlp could not download Instagram story {StoryUrl}: {Error}", storyUrl, stderr)
                    return None
                | Error error ->
                    Log.Warning("Could not run yt-dlp for Instagram story {StoryUrl}: {Error}", storyUrl, error)
                    return None
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

    let private downloadPost pId mediaIndex =
        async {
            let useProxy = Telebot.HttpClient.ProxyConfig.useProxyForInstagramPosts()
            let! modernMedia = fetchModernPostMedia pId useProxy

            match modernMedia with
            | Some (items, caption) ->
                let selectedItems =
                    match mediaIndex with
                    | Some index -> items |> List.tryItem (index - 1) |> Option.toList
                    | None -> items

                if List.isEmpty selectedItems then
                    return Reply.createMessage "Instagram carousel item not found"
                else
                    let! downloaded =
                        selectedItems
                        |> List.map (fun (url, isVideo) -> downloadMediaAsync url isVideo useProxy)
                        |> Async.Parallel
                    return Reply.createGallery downloaded caption
            | None ->
                let! media = fetchMediaData pId useProxy
                match media.Data |> Option.bind _.InstagramXdt with
                | Some xdt ->
                    let! mediaItems =
                        match xdt.EdgeSidecarToChildren with
                        | Some { Edges = edges } when not edges.IsEmpty ->
                            let selectedEdges =
                                match mediaIndex with
                                | Some index -> edges |> List.tryItem (index - 1) |> Option.toList
                                | None -> edges

                            selectedEdges
                            |> List.map (fun e ->
                                let downloadUrl =
                                    if e.Node.IsVideo then e.Node.VideoUrl
                                    else e.Node.DisplayUrl

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
                            | Some value -> [| downloadMediaWithAudioAsync value audioUrl xdt.IsVideo useProxy |]
                            | None -> [||]
                            |> Async.Parallel

                    return Reply.createGallery mediaItems (Some(getCaption xdt))
                | None ->
                    Log.Information $"GraphQL query failed for post {pId}. Trying proxy fallback..."
                    match mediaIndex with
                    | None ->
                        let! gallery = tryDownloadCarouselViaProxy pId useProxy
                        if not (Array.isEmpty gallery) then
                            return Reply.createGallery gallery None
                        else
                            let! proxyVideo = tryDownloadMediaViaProxyPath $"/p/{pId}/" true useProxy
                            match proxyVideo with
                            | Some media -> return Reply.createGallery [| media |] None
                            | None ->
                                let! proxyPhoto = tryDownloadMediaViaProxyPath $"/p/{pId}/" false useProxy
                                match proxyPhoto with
                                | Some media -> return Reply.createGallery [| media |] None
                                | None -> return Reply.createMessage "Failed to download post"
                    | Some index ->
                        let path = $"/p/{pId}/?img_index={index}"
                        match! tryGetMediaViaProxyPath path None useProxy with
                        | Some (url, isVideo) ->
                            let! media = downloadMediaAsync url isVideo useProxy
                            return Reply.createGallery [| media |] None
                        | None -> return Reply.createMessage "Failed to download post"
        }

    let private downloadStory (username: string) (mediaId: string) =
        async {
            let useProxy = Telebot.HttpClient.ProxyConfig.useProxyForInstagramReels()

            let downloadViaProxy () =
                async {
                    let canonicalUrl = $"https://www.instagram.com/stories/{username}/{mediaId}/"
                    let! ytDlpMedia = tryDownloadStoryWithYtDlp canonicalUrl useProxy
                    match ytDlpMedia with
                    | Some gallery -> return Reply.createGallery [| gallery |] None
                    | None ->
                        Log.Information("Trying proxy fallback for Instagram story {StoryId}...", mediaId)
                        let path = $"/stories/{username}/{mediaId}/"
                        let! proxyVideoUrl = tryGetMediaUrlViaProxyPath path true useProxy
                        match proxyVideoUrl with
                        | Some url ->
                            let! gallery = downloadMediaAsync url true useProxy
                            return Reply.createGallery [| gallery |] None
                        | None ->
                            let! proxyImageUrl = tryGetMediaUrlViaProxyPath path false useProxy
                            match proxyImageUrl with
                            | Some url ->
                                let! gallery = downloadMediaAsync url false useProxy
                                return Reply.createGallery [| gallery |] None
                            | None -> return Reply.createMessage "Failed to download story (it may have expired or require Instagram cookies)"
                }

            let! graphqlMedia =
                async {
                    try
                        match mediaIdToShortcode mediaId with
                        | Some shortcode ->
                            let! media = fetchMediaData shortcode useProxy
                            return media.Data |> Option.bind _.InstagramXdt
                        | None -> return None
                    with ex ->
                        Log.Warning(ex, "Could not resolve Instagram story {StoryId} through GraphQL", mediaId)
                        return None
                }

            match graphqlMedia with
            | Some xdt ->
                let mediaUrl = if xdt.IsVideo then xdt.VideoUrl else xdt.ImageUrl
                match mediaUrl with
                | Some url ->
                    let audioUrl = if xdt.IsVideo then findAudioUrl xdt.DashInfo xdt.ClipsMetadata else None
                    let! gallery = downloadMediaWithAudioAsync url audioUrl xdt.IsVideo useProxy
                    return Reply.createGallery [| gallery |] None
                | None -> return! downloadViaProxy ()
            | None -> return! downloadViaProxy ()
        }

    let getInstagramReplyAsync url =
        async {
            try
                match url with
                | PostType(Reel id) ->
                    let! res = downloadReel id
                    return Success res
                | PostType(Post(id, mediaIndex)) ->
                    let! res = downloadPost id mediaIndex
                    return Success res
                | PostType(Story(username, mediaId)) ->
                    let! res = downloadStory username mediaId
                    return Success res
                | _ -> return InvalidUrl
            with
            | ex ->
                Log.Error(ex, "Error processing Instagram URL: {Url}", url)
                return DownloadError "Failed to download Instagram media. Please try again later."
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
                | PostType(Post(id, mediaIndex)) ->
                    let useProxy = Telebot.HttpClient.ProxyConfig.useProxyForInstagramPosts()
                    let! media = fetchMediaData id useProxy
                    let! videoUrlOpt =
                        async {
                            let xdt = media.Data |> Option.bind _.InstagramXdt
                            match xdt, mediaIndex with
                            | Some post, Some index ->
                                match post.EdgeSidecarToChildren |> Option.bind (fun sidecar -> sidecar.Edges |> List.tryItem (index - 1)) with
                                | Some edge when edge.Node.IsVideo -> return Some edge.Node.VideoUrl
                                | _ -> return None
                            | Some xdt, _ when xdt.IsVideo -> return xdt.VideoUrl
                            | _ ->
                                let path =
                                    match mediaIndex with
                                    | Some index -> $"/p/{id}/?img_index={index}"
                                    | None -> $"/p/{id}/"
                                return! tryGetMediaUrlViaProxyPath path true useProxy
                        }
                    match videoUrlOpt with
                    | Some vurl ->
                        let! res = extractAudioFromVideoAsync vurl (Some id) useProxy
                        match res with
                        | Choice1Of2 audioPath -> return Some (Reply.createAudioFile audioPath)
                        | Choice2Of2 msg -> return Some (Reply.createMessage msg)
                    | None -> return Some (Reply.createMessage "No video to extract audio from")
                | _ -> return Some (Reply.createMessage "Invalid Instagram URL for audio extraction")
            with ex ->
                Log.Error(ex, "Error extracting audio from Instagram URL: {Url}", url)
                return Some (Reply.createMessage "Failed to extract Instagram audio. Please try again later.")
        }


type InstagramLinksHandler() =
    inherit BaseHandler()
    member private this.getInstagramShareLinks (message: string option) = getLinks Instagram.shareRegex message
    member private this.getInstagramLinks (message: string option) = getLinks Instagram.postRegex message
    member private this.getInstagramStoryLinks (message: string option) = getLinks Instagram.storyRegex message
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
    member private this.extractInstagramStoryLinks =
        createLinkExtractor this.getInstagramStoryLinks InstagramMessage
    [<WolverineHandler>]
    member this.HandleLinks(msg: UpdateMessage) : Task =
        let links = this.extractInstagramVideoLinks msg @ this.extractInstagramStoryLinks msg
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
    
