module Telebot.Youtube

open System
open System.IO
open System.Linq
open System.Threading.Tasks
open System.Text.RegularExpressions
open System.Text.Json
open System.Text.Json.Nodes
open Serilog
open Telebot.Bus
open Telebot.Handlers
open Telebot.PrometheusMetrics
open Telebot.Messages
open Telebot.Replies
open Telebot.Replies.Reply
open Telebot.Ffmpeg
open Wolverine.Attributes

module Youtube =
    let youtubeRegex =
        Regex(
            @"https:\/\/(youtu\.be\/[a-zA-Z0-9_-]+|(?:www\.)?youtube\.com\/(watch\?v=[a-zA-Z0-9_-]+|shorts\/[a-zA-Z0-9_-]+))",
            RegexOptions.Compiled
        )

    let private isAudioRequested (text: string) =
        text.IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0 ||
        text.IndexOf("mp3", StringComparison.OrdinalIgnoreCase) >= 0 ||
        text.IndexOf("music", StringComparison.OrdinalIgnoreCase) >= 0

    let getYoutubeAudioLinks (message: string option) =
        match message with
        | Some text when isAudioRequested text -> getLinks youtubeRegex message
        | _ -> List.empty

    let getYoutubeVideoLinks (message: string option) =
        match message with
        | Some text when not (isAudioRequested text) -> getLinks youtubeRegex message
        | _ -> List.empty

    let private runProcessAsync fileName args =
        async {
            match! runProcessCaptureAsync fileName args 300_000 with
            | Ok result -> return result
            | Error error -> return -1, "", error
        }

    let private bytesInMiB = 1024L * 1024L
    let private sizeLimitBytes = 50L * bytesInMiB
    let private maxDownloadSizeBeforeReencode = 500L * bytesInMiB

    // Resolve tool executable path robustly (Windows adds .exe)
    let private resolveToolPath (baseName: string) =
        let exeName =
            if OperatingSystem.IsWindows() then
                if baseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) then baseName else baseName + ".exe"
            else baseName
        try
            let dirs =
                [
                    try Some (Directory.GetCurrentDirectory()) with _ -> None
                    try Some AppContext.BaseDirectory with _ -> None
                    try Some (Path.Combine(AppContext.BaseDirectory, "tools")) with _ -> None
                ]
                |> List.choose id
            let pathEnv = Environment.GetEnvironmentVariable("PATH")
            let sep = if OperatingSystem.IsWindows() then ';' else ':'
            let pathDirs =
                if String.IsNullOrWhiteSpace(pathEnv) then []
                else pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries) |> Array.toList
            let candidates = seq {
                for d in dirs do yield Path.Combine(d, exeName)
                for d in pathDirs do yield Path.Combine(d, exeName)
                yield exeName // fallback to shell lookup
            }
            candidates |> Seq.tryFind File.Exists |> Option.defaultValue exeName
        with _ -> exeName

    // Specific tool paths
    let private ytDlpExe = resolveToolPath "yt-dlp"
    let private ffmpegExe = resolveToolPath "ffmpeg"

    // Resolve cookies path if configured or present (see Telebot.Config)
    let private cookiesArg =
        match Config.get().YoutubeCookiesPath with
        | Some path -> $" --cookies \"{path}\""
        | None -> ""

    let private proxyArg () =
        if HttpClient.ProxyConfig.useProxyForYoutube() then
            match HttpClient.ProxyConfig.getProxyUrl() with
            | Some url -> $" --proxy \"{url}\""
            | None -> ""
        else ""

    do
        Log.Information("Using yt-dlp executable: {ytDlpExe}", ytDlpExe)
        Log.Information("Using ffmpeg executable: {ffmpegExe}", ffmpegExe)

    type YtFormat = {
        format_id: string
        ext: string option
        vcodec: string option
        acodec: string option
        tbr: float option // total bitrate kbps for progressive or per-stream for video-only
        abr: float option // audio bitrate kbps
        vbr: float option // video bitrate kbps
        filesize: int64 option
        filesize_approx: int64 option
        width: int option
        height: int option
        language: string option // language code for the stream, if provided
        audio_is_original: bool option // whether this audio track is marked as original
        audio_track_id: string option // yt-dlp's audio_track.id if present
        audio_track_name: string option // e.g., "Original audio", "Dub" if present
    }

    // Resolves a dotted path ("a.b") to a node, returning None for missing keys or JSON nulls
    let private selectNode (node: JsonNode) (path: string) : JsonNode option =
        path.Split('.')
        |> Array.fold (fun (current: JsonNode option) segment ->
            current
            |> Option.bind (fun parent ->
                match parent[segment] with
                | null -> None
                | value ->
                    if value.GetValueKind() = JsonValueKind.Null then None else Some value)) (Some node)

    let internal parseFormats (json: JsonNode) =
        match json["formats"] with
        | null -> Array.empty
        | formatsToken ->
            formatsToken.AsArray()
            |> Seq.toArray
            |> Array.choose (fun f ->
                try
                    // Reads a value as a string option, mapping missing keys and JSON nulls to None
                    let opt (name: string) = selectNode f name |> Option.map _.ToString()
                    let str = opt
                    let parseWith parse name = opt name |> Option.bind (parse >> function true, v -> Some v | _ -> None)
                    let floatOpt name = parseWith Double.TryParse name
                    let int64Opt name = parseWith Int64.TryParse name
                    let intOpt name = parseWith Int32.TryParse name
                    // Some extractors use 'language', others 'lang', occasionally nested under 'audio_lang'
                    let lang =
                        opt "language"
                        |> Option.orElse (opt "lang")
                        |> Option.orElse (opt "audio_lang")
                    let atId = opt "audio_track.id"
                    let atName = opt "audio_track.name"
                    let note = opt "format_note"
                    let formatStr = opt "format"
                    let urlStr = opt "url"

                    let isOrig =
                        let low s = if String.IsNullOrWhiteSpace(s) then "" else s.ToLowerInvariant()
                        let n = atName |> Option.defaultValue "" |> low
                        let i = atId |> Option.defaultValue "" |> low
                        let l = lang |> Option.defaultValue "" |> low
                        let fn = note |> Option.defaultValue "" |> low
                        let fmt = formatStr |> Option.defaultValue "" |> low
                        let u = urlStr |> Option.defaultValue "" |> low
                        // Detect "original" from multiple possible places: audio_track, language, format_note, format string, or URL xtags (acont=original)
                        let hasOrig =
                            n.Contains("original") || i = "original" || l = "original" ||
                            fn.Contains("original") || fmt.Contains("original") ||
                            u.Contains("acont%3Doriginal") || u.Contains("acont=original")
                        // Try to avoid commentary/dub tracks even if marked original in some fields
                        let looksDub =
                            n.Contains("dub") || n.Contains("description") || n.Contains("commentary") || n.Contains("narration") || fn.Contains("dub")
                        if hasOrig && not looksDub then Some true else None

                    Some {
                        format_id = str "format_id" |> Option.defaultValue ""
                        ext = str "ext"
                        vcodec = str "vcodec"
                        acodec = str "acodec"
                        tbr = floatOpt "tbr"
                        abr = floatOpt "abr"
                        vbr = floatOpt "vbr"
                        filesize = int64Opt "filesize"
                        filesize_approx = int64Opt "filesize_approx"
                        width = intOpt "width"
                        height = intOpt "height"
                        language = lang
                        audio_is_original = isOrig
                        audio_track_id = atId
                        audio_track_name = atName
                    }
                with _ -> None)

    let private estimateSize (durationSec: float) (v: YtFormat option) (a: YtFormat option) =
        let sizeFromBitrate kbps =
            // bytes = kbps * 1000/8 * seconds
            int64 (kbps * 1000.0 / 8.0 * durationSec)
        let vSize =
            match v with
            | Some vf -> vf.filesize |> Option.orElse vf.filesize_approx |> Option.orElse (vf.vbr |> Option.orElse vf.tbr |> Option.map sizeFromBitrate)
            | None -> None
        let aSize =
            match a with
            | Some af -> af.filesize |> Option.orElse af.filesize_approx |> Option.orElse (af.abr |> Option.orElse af.tbr |> Option.map sizeFromBitrate)
            | None -> None
        match vSize, aSize with
        | Some vs, Some asz -> Some (vs + asz)
        | Some vs, None -> Some vs
        | None, Some asz -> Some asz
        | None, None -> None

    let internal pickBestCombo (durationSec: float) (formats: YtFormat array) =
        let videos =
            formats
            |> Array.filter (fun f -> f.vcodec |> Option.exists (fun v -> v <> "none") && f.acodec |> Option.exists (fun a -> a = "none"))
        let audios =
            formats
            |> Array.filter (fun f -> f.acodec |> Option.exists (fun a -> a <> "none") && f.vcodec |> Option.exists (fun v -> v = "none"))
        if videos.Length = 0 || audios.Length = 0 then None else
        let isAvc1 f = f.vcodec |> Option.exists (fun v -> v.StartsWith("avc1") || v.ToLower().Contains("h264"))
        let isMp4 f = f.ext |> Option.exists (fun e -> e.Equals("mp4", StringComparison.OrdinalIgnoreCase) || e.Equals("m4v", StringComparison.OrdinalIgnoreCase))
        let score f =
            let res = defaultArg f.height 0 * 10000 + defaultArg f.width 0
            let codecBonus = if isAvc1 f then 100_000_000 else 0
            let extBonus = if isMp4 f then 10_000_000 else 0
            res + codecBonus + extBonus
        // Order videos by preference
        let orderedVideos = videos |> Array.sortByDescending score
        let orderedAudios =
            let originalScore (a: YtFormat) =
                let low (s:string) = if String.IsNullOrWhiteSpace(s) then "" else s.ToLowerInvariant()
                match a.audio_is_original with
                | Some true -> 3
                | _ ->
                    let n = a.audio_track_name |> Option.defaultValue "" |> low
                    let i = a.audio_track_id |> Option.defaultValue "" |> low
                    let l = a.language |> Option.defaultValue "" |> low
                    let hasOrig = n.Contains("original") || i = "original" || l = "original"
                    let looksDub = n.Contains("dub") || n.Contains("description") || n.Contains("commentary") || n.Contains("narration")
                    if hasOrig && not looksDub then 2
                    elif looksDub then -1
                    else 0
            audios
            |> Array.sortByDescending (fun a ->
                let abrOrTbr = defaultArg a.abr (defaultArg a.tbr 0.0)
                (originalScore a, abrOrTbr, if a.ext = Some "m4a" then 1 else 0))
        // Try combinations until under limit
        let mutable chosen : (YtFormat * YtFormat * int64 option) option = None
        for v in orderedVideos do
            if chosen.IsNone then
                for a in orderedAudios do
                    if chosen.IsNone then
                        let total = estimateSize durationSec (Some v) (Some a)
                        match total with
                        | Some t when t <= sizeLimitBytes ->
                            chosen <- Some (v, a, Some t)
                        | _ -> ()
        // If nothing fits, try to pick the smallest video+audio under limit by bitrate estimation
        match chosen with
        | Some _ -> chosen
        | None ->
            // sort ascending by estimated size and pick first under limit; else pick very small fallback
            let combos =
                [ for v in orderedVideos do
                    for a in orderedAudios do
                        let est = estimateSize durationSec (Some v) (Some a)
                        yield (v, a, est) ]
            combos
            |> List.sortBy (fun (_,_,est) -> est |> Option.defaultValue Int64.MaxValue)
            |> List.tryFind (fun (_,_,est) -> est |> Option.exists (fun t -> t <= sizeLimitBytes))
            |> Option.orElse (combos |> List.tryLast)

    let private getJsonAsync (url: string) =
        async {
            let args = $"-J --no-warnings --no-simulate --no-check-certificates{cookiesArg}{proxyArg()} \"{url}\""
            let! code, stdout, stderr = runProcessAsync ytDlpExe args
            if code <> 0 then
                Log.Warning("yt-dlp -J failed: {stderr}", stderr)
                return None
            else
                try return Some (JsonNode.Parse stdout)
                with ex ->
                    Log.Error(ex, "Failed to parse yt-dlp JSON")
                    return None
        }

    let private safeDelete (path: string) =
        try
            if File.Exists path then
                File.Delete path
                Log.Information("Deleted file during cleanup: {path}", path)
        with ex -> Log.Warning(ex, "Failed to delete file during cleanup: {path}", path)

    let private deleteMatching (dir: string) (predicate: string -> bool) =
        try
            if Directory.Exists dir then
                for f in Directory.GetFiles(dir) do
                    try if predicate f then safeDelete f with _ -> ()
        with ex -> Log.Warning(ex, "Cleanup scan failed for dir: {dir}", dir)

    let private cleanupArtifacts (outFile: string) (videoId: string option) (vId: string option) (aId: string option) =
        try
            let dir =
                try
                    let d = Path.GetDirectoryName(outFile)
                    if String.IsNullOrWhiteSpace(d) then Directory.GetCurrentDirectory() else d
                with _ -> Directory.GetCurrentDirectory()
            let baseNoExt =
                try Path.GetFileNameWithoutExtension(outFile) with _ -> outFile
            // delete temp variants tied to our base name
            [ Path.ChangeExtension(outFile, ".temp.mp4")
              Path.ChangeExtension(outFile, ".smaller.mp4") ]
            |> List.iter safeDelete
            // delete yt-dlp part files for selected format ids
            let partsPred (p:string) =
                let name = Path.GetFileName(p)
                let hasVid = vId |> Option.exists (fun id -> name.Contains($".f{id}", StringComparison.OrdinalIgnoreCase))
                let hasAid = aId |> Option.exists (fun id -> name.Contains($".f{id}", StringComparison.OrdinalIgnoreCase))
                let hasBase = name.StartsWith(baseNoExt + ".f", StringComparison.OrdinalIgnoreCase)
                hasVid || hasAid || hasBase
            deleteMatching dir partsPred
            // delete any orphan files that contain the YouTube video id in their name (e.g., title [id].webm)
            match videoId with
            | Some vid ->
                let orphanPred (p:string) =
                    let name = Path.GetFileName(p)
                    let ext = Path.GetExtension(p).ToLowerInvariant()
                    let isMedia = ext = ".webm" || ext = ".mkv" || ext = ".m4a" || ext = ".mp4" || ext = ".m4v" || ext = ".part"
                    let containsId = name.Contains(vid, StringComparison.OrdinalIgnoreCase)
                    let isOut = String.Equals(Path.GetFullPath(p), Path.GetFullPath(outFile), StringComparison.OrdinalIgnoreCase)
                    isMedia && containsId && (not isOut)
                deleteMatching dir orphanPred
            | None -> ()
        with ex -> Log.Warning(ex, "CleanupArtifacts encountered an error")

    let private runYtDlpDownloadAsync (url: string) (vId: string) (aId: string) (outFile: string) =
        async {
            // Prefer merge to mp4; provide ffmpeg location if we know it
            let ffmpegLocArg =
                try
                    if File.Exists ffmpegExe then
                        let dir = Path.GetDirectoryName(ffmpegExe)
                        if String.IsNullOrWhiteSpace(dir) then "" else $" --ffmpeg-location \"{dir}\""
                    else ""
                with _ -> ""
            let args = $"-f {vId}+{aId} --merge-output-format mp4{ffmpegLocArg}{cookiesArg}{proxyArg()} -o \"{outFile}\" \"{url}\""
            let! code1, _o1, e1 = runProcessAsync ytDlpExe args
            // Helper to try manual merge if yt-dlp left separate files like out.f398.mp4 and out.f139.m4a
            let tryManualMergeAsync () =
                async {
                    try
                        let dir = Path.GetDirectoryName(outFile)
                        let dir = if String.IsNullOrWhiteSpace(dir) then Directory.GetCurrentDirectory() else dir
                        let baseNameNoExt = Path.GetFileNameWithoutExtension(outFile)
                        let candidates = Directory.GetFiles(dir, baseNameNoExt + ".f*.*")
                        let vPath = candidates |> Array.tryFind _.Contains($".f{vId}")
                        let aPath = candidates |> Array.tryFind _.Contains($".f{aId}")
                        match vPath, aPath with
                        | Some vp, Some ap ->
                            let ffArgs = $"-y -i \"{vp}\" -i \"{ap}\" -c:v copy -c:a copy -movflags +faststart \"{outFile}\""
                            let! c, _o, e = runProcessAsync ffmpegExe ffArgs
                            if c = 0 && File.Exists outFile then
                                try File.Delete(vp) with _ -> ()
                                try File.Delete(ap) with _ -> ()
                                return true
                            else
                                Log.Error("Manual ffmpeg merge failed: {err}", e)
                                return false
                        | _ -> return false
                    with ex ->
                        Log.Error(ex, "Error while attempting manual merge of yt-dlp parts")
                        return false
                }

            if code1 = 0 && File.Exists outFile then return true, outFile
            elif File.Exists outFile then return true, outFile
            else
                let! merged = tryManualMergeAsync ()
                if merged && File.Exists outFile then return true, outFile
                else
                    Log.Warning("yt-dlp merge failed or file missing, trying recode to mp4: {err}", e1)
                    let tmpName = Path.ChangeExtension(outFile, ".temp.mp4")
                    let args2 = $"-f {vId}+{aId} --recode-video mp4{ffmpegLocArg}{cookiesArg}{proxyArg()} -o \"{tmpName}\" \"{url}\""
                    let! code2, _o2, e2 = runProcessAsync ytDlpExe args2
                    if code2 = 0 && File.Exists tmpName then
                        try
                            if File.Exists outFile then File.Delete outFile
                        with _ -> ()
                        File.Move(tmpName, outFile, true)
                        return true, outFile
                    else
                        Log.Error("yt-dlp recode failed: {err}", e2)
                        try safeDelete tmpName with _ -> ()
                        cleanupArtifacts outFile None (Some vId) (Some aId)
                        return false, outFile
        }

    let getYoutubeReply (url: string) =
        async {
            try
                match! getJsonAsync url with
                | None ->
                    let msg = createMessage "Failed to fetch video info"
                    youtubeFailureCounter.Inc()
                    return Some msg
                | Some json ->
                    let title = json["title"] |> Option.ofObj |> Option.map _.ToString()
                    let id = json["id"] |> Option.ofObj |> Option.map _.ToString() |> Option.defaultValue (Guid.NewGuid().ToString("N"))
                    let duration =
                        match json["duration"] with
                        | null -> 0.0
                        | v ->
                            match Double.TryParse(v.ToString()) with
                            | true, d -> d
                            | _ -> 0.0
                    let formats = parseFormats json
                    Log.Information("Found {count} formats for YouTube video {id}", formats.Length, id)
                    match pickBestCombo duration formats with
                    | None ->
                        let message = createMessage "Cannot download video due size limits - max size is 50 MiB"
                        youtubeFailureCounter.Inc()
                        return Some message
                    | Some (v, a, est) ->
                        let fileName = $"yt_{id}_{Guid.NewGuid()}.mp4"
                        Log.Information("Downloading YouTube: {title} using v={v} a={a} est={est}", title |> Option.defaultValue "", v.format_id, a.format_id, est)
                        let! ok, path = runYtDlpDownloadAsync url v.format_id a.format_id fileName
                        if not ok then
                            cleanupArtifacts fileName (Some id) (Some v.format_id) (Some a.format_id)
                            let message = createMessage "Failed to download or convert video"
                            youtubeFailureCounter.Inc()
                            return Some message
                        else
                            let fi = FileInfo(path)
                            if fi.Length > sizeLimitBytes then
                                Log.Warning("Downloaded file exceeds limit: {len} bytes > {limit}", fi.Length, sizeLimitBytes)
                                if fi.Length > maxDownloadSizeBeforeReencode then
                                    Log.Warning("Downloaded file size {len} bytes exceeds the maximum {max} bytes for downscaling. Skipping re-encoding.", fi.Length, maxDownloadSizeBeforeReencode)
                                else
                                    // Try to downscale via ffmpeg quick re-encode with bitrate based on duration
                                    if duration > 0.0 then
                                        let targetBytes = sizeLimitBytes
                                        let audioKbps = defaultArg a.abr (defaultArg a.tbr 128.0)
                                        // leave ~25% for audio
                                        let audioBytes = int64 (audioKbps * 1000.0 / 8.0 * duration)
                                        let videoBytes = max 1L (targetBytes - audioBytes)
                                        let videoKbps = max 250.0 (float videoBytes * 8.0 / 1000.0 / duration)
                                        let tmp = Path.ChangeExtension(path, ".smaller.mp4")
                                        let encoderArgs = videoEncoderArgs ()
                                        let encoder = videoEncoderName ()
                                        let ffArgs = $"-y {encoderArgs} -i \"{path}\" -c:v {encoder} -b:v {videoKbps:F0}k -c:a copy -movflags +faststart \"{tmp}\""
                                        let! encodeResult = runVideoEncodeAsync ffmpegExe ffArgs 300_000
                                        let code, _o, e =
                                            match encodeResult with
                                            | Ok result -> result
                                            | Error error -> -1, "", error
                                        if code = 0 && File.Exists tmp then
                                            try File.Delete path with _ -> ()
                                            File.Move(tmp, path, true)
                                        else
                                            Log.Error("ffmpeg size reduction failed: {err}", e)
                                ()
                            let finalSize = (FileInfo(path)).Length
                            if finalSize > sizeLimitBytes then
                                let message = createMessage "Cannot download video due size limits - max size is 50 MiB"
                                try File.Delete path with _ -> ()
                                cleanupArtifacts fileName (Some id) (Some v.format_id) (Some a.format_id)
                                youtubeFailureCounter.Inc()
                                return Some message
                            else
                                // Success path: proactively remove any orphan/part artifacts for this video id
                                cleanupArtifacts fileName (Some id) (Some v.format_id) (Some a.format_id)
                                let reply = createVideoFileWithCaption path title
                                youtubeSuccessCounter.Inc()
                                return Some reply
            with ex ->
                Log.Error(ex, "Error processing YouTube video")
                youtubeFailureCounter.Inc()
                return None
        }

    let getYoutubeAudioReply (url: string) =
        async {
            try
                match! getJsonAsync url with
                | None ->
                    let msg = createMessage "Failed to fetch video info"
                    youtubeFailureCounter.Inc()
                    return Some msg
                | Some json ->
                    let id = json["id"] |> Option.ofObj |> Option.map _.ToString() |> Option.defaultValue (Guid.NewGuid().ToString("N"))
                    let formats = parseFormats json
                    let audios = formats |> Array.filter (fun f -> f.acodec |> Option.exists (fun a -> a <> "none") && f.vcodec |> Option.exists (fun v -> v = "none"))
                    if audios.Length = 0 then
                        youtubeFailureCounter.Inc()
                        return Some (createMessage "No audio-only formats found")
                    else
                        let best = audios |> Array.sortByDescending (fun a -> defaultArg a.abr 0.0) |> Array.head
                        let ext = defaultArg best.ext "m4a"
                        let fileName = $"yt_{id}_{Guid.NewGuid()}.{ext}"
                        let args = $"-f {best.format_id}{cookiesArg}{proxyArg()} -o \"{fileName}\" \"{url}\""
                        let! code, _o, e = runProcessAsync ytDlpExe args
                        if code <> 0 || not (File.Exists fileName) then
                            Log.Error("yt-dlp audio download failed: {err}", e)
                            youtubeFailureCounter.Inc()
                            return Some (createMessage "Failed to download audio")
                        else
                            youtubeSuccessCounter.Inc()
                            return Some (createAudioFile fileName)
            with ex ->
                Log.Error(ex, "Error processing YouTube audio")
                youtubeFailureCounter.Inc()
                return None
        }


type YoutubeLinksHandler() =
    inherit BaseHandler()
    member private this.extractYoutubeAudioLinks =
        createLinkExtractor Youtube.getYoutubeAudioLinks YoutubeAudioMessage
    member private this.extractYoutubeVideoLinks =
        createLinkExtractor Youtube.getYoutubeVideoLinks YoutubeMessage
    [<WolverineHandler>]
    member this.HandleAudioLinks(msg: UpdateMessage) : Task =
        let links = this.extractYoutubeAudioLinks msg
        task {
            for message in links do
                do! publishToBusAsync message |> Async.StartAsTask
        }
    [<WolverineHandler>]
    member this.HandleVideoLinks(msg: UpdateMessage) : Task =
        let links = this.extractYoutubeVideoLinks msg
        task {
            for message in links do
                do! publishToBusAsync message |> Async.StartAsTask
        }
    member this.Handle(msg: YoutubeAudioMessage) =
        this.processLinkAsync msg Youtube.getYoutubeAudioReply
    member this.Handle(msg: YoutubeMessage) =
        this.processLinkAsync msg Youtube.getYoutubeReply
