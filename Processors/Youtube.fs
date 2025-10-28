module Telebot.Youtube

open System
open System.Diagnostics
open System.IO
open System.Linq
open System.Text
open System.Text.RegularExpressions
open Newtonsoft.Json.Linq
open Serilog
open Telebot.Bus
open Telebot.Handlers
open Telebot.Messages
open Telebot.Text
open Telebot.Text.Reply

module private Youtube =
    let private youtubeRegex =
        Regex(
            @"https:\/\/(youtu\.be\/[a-zA-Z0-9_-]+|(?:www\.)?youtube\.com\/(watch\?v=[a-zA-Z0-9_-]+|shorts\/[a-zA-Z0-9_-]+))",
            RegexOptions.Compiled
        )

    let getYoutubeLinks (message: string option) = getLinks youtubeRegex message

    let private runProcess (fileName: string) (args: string) (workingDir: string option) : int * string * string =
        try
            let psi = ProcessStartInfo()
            psi.FileName <- fileName
            psi.Arguments <- args
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            workingDir |> Option.iter (fun d -> psi.WorkingDirectory <- d)
            use p = new Process()
            p.StartInfo <- psi
            let stdout = StringBuilder()
            let stderr = StringBuilder()
            p.OutputDataReceived.Add(fun de -> if not (isNull de.Data) then stdout.AppendLine(de.Data) |> ignore)
            p.ErrorDataReceived.Add(fun de -> if not (isNull de.Data) then stderr.AppendLine(de.Data) |> ignore)
            if not (p.Start()) then
                (-1, stdout.ToString(), "Failed to start process")
            else
                p.BeginOutputReadLine()
                p.BeginErrorReadLine()
                p.WaitForExit()
                (p.ExitCode, stdout.ToString(), stderr.ToString())
        with ex ->
            (-1, "", ex.ToString())

    let private bytesInMiB = 1024L * 1024L
    let private sizeLimitBytes = 50L * bytesInMiB

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

    let private tryGet (tok: JToken) (name: string) =
        if isNull tok then None else tok.SelectToken(name) |> Option.ofObj

    let private parseFormats (json: JToken) =
        json.SelectToken("formats")
        |> fun f -> if isNull f then Array.empty<JToken> else f.ToArray()
        |> Array.choose (fun f ->
            try
                let str name =
                    match f.SelectToken(name) with
                    | null -> None
                    | v when v.Type = JTokenType.Null -> None
                    | v -> Some (string v)
                let floatOpt name =
                    match f.SelectToken(name) with
                    | null -> None
                    | v when v.Type = JTokenType.Null -> None
                    | v ->
                        match Double.TryParse(string v) with
                        | true, d -> Some d
                        | _ -> None
                let int64Opt name =
                    match f.SelectToken(name) with
                    | null -> None
                    | v when v.Type = JTokenType.Null -> None
                    | v ->
                        match Int64.TryParse(string v) with
                        | true, d -> Some d
                        | _ -> None
                let intOpt name =
                    match f.SelectToken(name) with
                    | null -> None
                    | v when v.Type = JTokenType.Null -> None
                    | v ->
                        match Int32.TryParse(string v) with
                        | true, d -> Some d
                        | _ -> None
                // Some extractors use 'language', others 'lang', occasionally nested under 'audio_lang'
                let lang =
                    match f.SelectToken("language") with
                    | null ->
                        match f.SelectToken("lang") with
                        | null ->
                            match f.SelectToken("audio_lang") with
                            | null -> None
                            | v when v.Type = JTokenType.Null -> None
                            | v -> Some (string v)
                        | v when v.Type = JTokenType.Null -> None
                        | v -> Some (string v)
                    | v when v.Type = JTokenType.Null -> None
                    | v -> Some (string v)
                let atId =
                    match f.SelectToken("audio_track.id") with
                    | null -> None
                    | v when v.Type = JTokenType.Null -> None
                    | v -> Some (string v)
                let atName =
                    match f.SelectToken("audio_track.name") with
                    | null -> None
                    | v when v.Type = JTokenType.Null -> None
                    | v -> Some (string v)
                let isOrig =
                    let low s = if String.IsNullOrWhiteSpace(s) then "" else s.ToLowerInvariant()
                    let n = atName |> Option.defaultValue "" |> low
                    let i = atId |> Option.defaultValue "" |> low
                    let l = lang |> Option.defaultValue "" |> low
                    let hasOrig = n.Contains("original") || i = "original" || l = "original"
                    let looksDub = n.Contains("dub") || n.Contains("description") || n.Contains("commentary") || n.Contains("narration")
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

    let private pickBestCombo (durationSec: float) (formats: YtFormat array) =
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
            let codecBonus = if isAvc1 f then 1_000_000 else 0
            let extBonus = if isMp4 f then 100_000 else 0
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

    let private getJson (url: string) =
        let args = $"-J --no-warnings --no-simulate --no-check-certificates \"{url}\""
        let code, stdout, stderr = runProcess ytDlpExe args None
        if code <> 0 then
            Log.Warning("yt-dlp -J failed: {stderr}", stderr)
            None
        else
            try Some (JToken.Parse stdout) with ex -> Log.Error(ex, "Failed to parse yt-dlp JSON"); None

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

    let private runYtDlpDownload (url: string) (vId: string) (aId: string) (outFile: string) : bool * string =
        // Prefer merge to mp4; provide ffmpeg location if we know it
        let ffmpegLocArg =
            try
                if File.Exists ffmpegExe then
                    let dir = Path.GetDirectoryName(ffmpegExe)
                    if String.IsNullOrWhiteSpace(dir) then "" else $" --ffmpeg-location \"{dir}\""
                else ""
            with _ -> ""
        let args = $"-f {vId}+{aId} --merge-output-format mp4{ffmpegLocArg} -o \"{outFile}\" \"{url}\""
        let code1, _o1, e1 = runProcess ytDlpExe args None
        // Helper to try manual merge if yt-dlp left separate files like out.f398.mp4 and out.f139.m4a
        let tryManualMerge () =
            try
                let dir = Path.GetDirectoryName(outFile)
                let dir = if String.IsNullOrWhiteSpace(dir) then Directory.GetCurrentDirectory() else dir
                let baseNameNoExt = Path.GetFileNameWithoutExtension(outFile)
                let candidates = Directory.GetFiles(dir, baseNameNoExt + ".f*.*")
                let vPath = candidates |> Array.tryFind (fun p -> p.Contains($".f{vId}"))
                let aPath = candidates |> Array.tryFind (fun p -> p.Contains($".f{aId}"))
                match vPath, aPath with
                | Some vp, Some ap ->
                    let ffArgs = $"-y -i \"{vp}\" -i \"{ap}\" -c:v copy -c:a copy -movflags +faststart \"{outFile}\""
                    let c, _o, e = runProcess ffmpegExe ffArgs None
                    if c = 0 && File.Exists outFile then
                        // Cleanup parts
                        try File.Delete(vp) with _ -> ()
                        try File.Delete(ap) with _ -> ()
                        true
                    else
                        Log.Error("Manual ffmpeg merge failed: {err}", e)
                        false
                | _ -> false
            with ex ->
                Log.Error(ex, "Error while attempting manual merge of yt-dlp parts")
                false
        if code1 = 0 && File.Exists outFile then true, outFile
        else if File.Exists outFile then true, outFile
        else
            // If yt-dlp succeeded but left parts due to missing ffmpeg, try manual merge
            let merged = tryManualMerge ()
            if merged && File.Exists outFile then true, outFile
            else
                Log.Warning("yt-dlp merge failed or file missing, trying recode to mp4: {err}", e1)
                let tmpName = Path.ChangeExtension(outFile, ".temp.mp4")
                let args2 = $"-f {vId}+{aId} --recode-video mp4{ffmpegLocArg} -o \"{tmpName}\" \"{url}\""
                let code2, _o2, e2 = runProcess ytDlpExe args2 None
                if code2 = 0 && File.Exists tmpName then
                    try
                        if File.Exists outFile then File.Delete outFile
                    with _ -> ()
                    File.Move(tmpName, outFile, true)
                    true, outFile
                else
                    Log.Error("yt-dlp recode failed: {err}", e2)
                    // Cleanup tmp and any parts
                    try safeDelete tmpName with _ -> ()
                    cleanupArtifacts outFile None (Some vId) (Some aId)
                    false, outFile

    let getYoutubeReply (url: string) =
        async {
            match getJson url with
            | None ->
                let msg = createMessage "Failed to fetch video info"
                return Some msg
            | Some json ->
                let title = json.SelectToken("title") |> Option.ofObj |> Option.map string
                let id = json.SelectToken("id") |> Option.ofObj |> Option.map string |> Option.defaultValue (Guid.NewGuid().ToString("N"))
                let duration =
                    match json.SelectToken("duration") with
                    | null -> 0.0
                    | v ->
                        match Double.TryParse(string v) with
                        | true, d -> d
                        | _ -> 0.0
                let formats = parseFormats json
                Log.Information("Found {count} formats for YouTube video {id}", formats.Length, id)
                match pickBestCombo duration formats with
                | None ->
                    let message = createMessage "Cannot download video due size limits - max size is 50 MiB"
                    return Some message
                | Some (v, a, est) ->
                    let fileName = $"yt_{id}_{Guid.NewGuid()}.mp4"
                    Log.Information("Downloading YouTube: {title} using v={v} a={a} est={est}", title |> Option.defaultValue "", v.format_id, a.format_id, est)
                    let ok, path = runYtDlpDownload url v.format_id a.format_id fileName
                    if not ok then
                        cleanupArtifacts fileName (Some id) (Some v.format_id) (Some a.format_id)
                        let message = createMessage "Failed to download or convert video"
                        return Some message
                    else
                        let fi = FileInfo(path)
                        if fi.Length > sizeLimitBytes then
                            Log.Warning("Downloaded file exceeds limit: {len} bytes > {limit}", fi.Length, sizeLimitBytes)
                            // Try to downscale via ffmpeg quick re-encode with bitrate based on duration
                            if duration > 0.0 then
                                let targetBytes = sizeLimitBytes
                                let audioKbps = defaultArg a.abr (defaultArg a.tbr 128.0)
                                // leave ~25% for audio
                                let audioBytes = int64 (audioKbps * 1000.0 / 8.0 * duration)
                                let videoBytes = max 1L (targetBytes - audioBytes)
                                let videoKbps = max 250.0 (float videoBytes * 8.0 / 1000.0 / duration)
                                let tmp = Path.ChangeExtension(path, ".smaller.mp4")
                                let ffArgs = $"-y -i \"{path}\" -c:v libx264 -b:v {videoKbps:F0}k -c:a copy -movflags +faststart \"{tmp}\""
                                let code, _o, e = runProcess ffmpegExe ffArgs None
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
                            return Some message
                        else
                            // Success path: proactively remove any orphan/part artifacts for this video id
                            cleanupArtifacts fileName (Some id) (Some v.format_id) (Some a.format_id)
                            let reply = createVideoFileWithCaption path title
                            return Some reply
        }
        |> Async.RunSynchronously


type YoutubeLinksHandler() =
    inherit BaseHandler()
    member private this.extractYoutubeLinks =
        createLinkExtractor Youtube.getYoutubeLinks YoutubeMessage
    member this.Handle(msg: UpdateMessage) =
        this.extractYoutubeLinks msg |> List.map publishToBus |> ignore
    member this.Handle(msg: YoutubeMessage) =
        this.processLink msg Youtube.getYoutubeReply
