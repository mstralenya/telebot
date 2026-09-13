module Telebot.Ffmpeg

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Telebot.PrometheusMetrics
open Telebot.TelemetryService

// Timeouts for external tool invocations
let internal probeTimeoutMs = 30_000
let internal ffmpegTimeoutMs = 15 * 60 * 1000
let private videoEncodeSemaphore =
    lazy (new SemaphoreSlim(Config.get().MaxParallelVideoEncodes))

let private vaapiConfig () =
    let config = Config.get ()
    if config.FfmpegVideoEncoder.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase) then
        config.FfmpegVaapiDevice
    else
        None

let internal videoEncoderArgs () =
    match vaapiConfig () with
    | Some device ->
        $"-hwaccel vaapi -hwaccel_device \"{device}\" -hwaccel_output_format vaapi -vaapi_device \"{device}\""
    | _ -> ""

let internal videoEncoderName () =
    let configured = Config.get().FfmpegVideoEncoder
    if configured.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase) && vaapiConfig().IsNone then
        "libx264"
    else
        configured

let internal videoScaleFilterArgs maxWidth =
    match vaapiConfig () with
    | Some _ -> $"-vf \"scale_vaapi=w='min({maxWidth},iw)':h=-2:format=nv12\""
    | None -> $"-vf \"scale='min({maxWidth},iw)':-2\""

let internal videoQualityArgs quality preset =
    match vaapiConfig () with
    | Some _ -> $"-qp {quality}"
    | None -> $"-crf {quality} -preset {preset}"

/// Runs an external process, draining stdout/stderr concurrently so a full pipe
/// buffer can never deadlock the child, and kills it when `timeoutMs` elapses.
/// Returns Ok (exitCode, stdout, stderr) or Error with a reason.
let internal runProcessCaptureAsync
    (fileName: string)
    (arguments: string)
    (timeoutMs: int) : Async<Result<int * string * string, string>> =
    async {
        let startInfo =
            ProcessStartInfo(
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            )

        use proc = new Process(StartInfo = startInfo)
        let started = proc.Start()

        if not started then
            return Error $"Failed to start process '{fileName}'"
        else
            let stdoutTask = proc.StandardOutput.ReadToEndAsync()
            let stderrTask = proc.StandardError.ReadToEndAsync()
            use timeout = new CancellationTokenSource(timeoutMs)

            try
                do! proc.WaitForExitAsync(timeout.Token) |> Async.AwaitTask
                let! stdout = stdoutTask |> Async.AwaitTask
                let! stderr = stderrTask |> Async.AwaitTask
                return Ok (proc.ExitCode, stdout, stderr)
            with :? OperationCanceledException ->
                try proc.Kill(true) with _ -> ()
                do! proc.WaitForExitAsync() |> Async.AwaitTask
                let! _ = stdoutTask |> Async.AwaitTask
                let! _ = stderrTask |> Async.AwaitTask
                return Error $"{fileName} timed out after {timeoutMs} ms and was killed"
    }

let internal runVideoEncodeAsync fileName arguments timeoutMs =
    async {
        let semaphore = videoEncodeSemaphore.Value
        do! semaphore.WaitAsync() |> Async.AwaitTask
        try
            return! runProcessCaptureAsync fileName arguments timeoutMs
        finally
            semaphore.Release() |> ignore
    }

let ensureVideoHasAudioAsync (filePath: string) : Async<unit> =
    withOperationTelemetry "ensure_video_audio" (fun scope ->
        async {
            try
                TelemetryScope.addProperty "file_path" filePath scope |> ignore
                let probeArgs = sprintf "-v error -show_entries stream=codec_type -of default=nw=1:nk=1 \"%s\"" filePath

                match! runProcessCaptureAsync "ffprobe" probeArgs probeTimeoutMs with
                | Error msg ->
                    TelemetryScope.logError None $"Failed to probe video streams: {msg}" scope
                | Ok (probeExitCode, probeOutput, probeError) ->
                    if probeExitCode <> 0 then
                        TelemetryScope.logError None $"ffprobe failed with exit code {probeExitCode}: {probeError}" scope
                    elif not (probeOutput.Contains("audio")) then
                        TelemetryScope.logInfo "No audio stream found, adding silent audio track" scope
                        let tempFilePath = $"{filePath}.temp.mp4"
                        let ffmpegArgs = sprintf "-y -v error -i \"%s\" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 -c:v copy -c:a aac -shortest \"%s\"" filePath tempFilePath

                        match! runProcessCaptureAsync "ffmpeg" ffmpegArgs ffmpegTimeoutMs with
                        | Error msg ->
                            TelemetryScope.logError None $"Failed to add silent audio track: {msg}" scope
                        | Ok (ffmpegExitCode, _, ffmpegError) ->
                            if ffmpegExitCode = 0 then
                                File.Move(tempFilePath, filePath, true)
                                TelemetryScope.logInfo "Silent audio track added successfully" scope
                            else
                                TelemetryScope.logError None $"Failed to add silent audio. ffmpeg error: {ffmpegError}" scope
                                if File.Exists tempFilePath then File.Delete tempFilePath
            with
            | ex ->
                TelemetryScope.logError (Some ex) "Error ensuring video has audio" scope
        }
    )

// Get video info asynchronously with better error handling
let getVideoInfoAsync (videoPath: string) : Async<(int64 * int64 * int64) option> =
    withOperationTelemetry "video_info_extraction" (fun scope ->
        async {
            try
                TelemetryScope.addProperty "video_path" videoPath scope |> ignore
                TelemetryScope.logInfo $"Extracting video info from {videoPath}" scope

                let arguments =
                    sprintf "-v error -select_streams v:0 -show_entries format=duration -show_entries stream=width,height -of default=noprint_wrappers=1:nokey=1 \"%s\"" videoPath

                match! runProcessCaptureAsync "ffprobe" arguments probeTimeoutMs with
                | Error msg ->
                    videoSizeFailureCounter.Inc()
                    TelemetryScope.logError None $"Failed to extract video info: {msg}" scope
                    return None
                | Ok (probeExitCode, output, error) ->
                    if probeExitCode = 0 then
                        let matches =
                            Regex.Match(output, @"(\d+)\n(\d+)\n(\d+)")

                        if matches.Success then
                            let results = [ 1..3 ] |> List.map (fun i -> int64 matches.Groups[i].Value)
                            let duration, width, height = results[2], results[0], results[1]

                            TelemetryScope.addProperty "duration" duration scope |> ignore
                            TelemetryScope.addProperty "width" width scope |> ignore
                            TelemetryScope.addProperty "height" height scope |> ignore
                            TelemetryScope.logInfo $"Video info extracted: Duration={duration}s, Resolution={width}x{height}" scope

                            videoSizeSuccessCounter.Inc()
                            return Some(duration, width, height)
                        else
                            videoSizeFailureCounter.Inc()
                            TelemetryScope.logWarning "Failed to parse video info output" scope
                            return None
                    else
                        videoSizeFailureCounter.Inc()
                        TelemetryScope.logError None $"ffprobe failed with exit code {probeExitCode}: {error}" scope
                        return None
            with
            | ex ->
                videoSizeFailureCounter.Inc()
                TelemetryScope.logError (Some ex) "Error extracting video info" scope
                return None
        }
    )

// Extract thumbnail asynchronously with better resource management
let extractThumbnailAsync (videoPath: string) (outputPath: string) : Async<bool> =
    withOperationTelemetry "thumbnail_extraction" (fun scope ->
        async {
            try
                TelemetryScope.addProperty "video_path" videoPath scope |> ignore
                TelemetryScope.addProperty "output_path" outputPath scope |> ignore
                TelemetryScope.logInfo $"Extracting thumbnail from {videoPath} to {outputPath}" scope

                let arguments =
                    sprintf """ -y -i "%s" -vf "blackframe=0,metadata=select:key=lavfi.blackframe.pblack:value=90:function=less,scale='if(gt(iw,ih),320,-1)':'if(gt(ih,iw),320,-1)'" -frames:v 1 -q:v 2 -update 1 "%s" """ videoPath outputPath

                match! runProcessCaptureAsync "ffmpeg" arguments ffmpegTimeoutMs with
                | Error error ->
                    thumbnailFailureCounter.Inc()
                    TelemetryScope.logError None $"Thumbnail extraction failed: {error}" scope
                    return false
                | Ok (exitCode, _, error) ->
                    if exitCode <> 0 then
                        thumbnailFailureCounter.Inc()
                        TelemetryScope.logError None $"Thumbnail extraction failed: {error}" scope
                        return false
                    else
                        thumbnailSuccessCounter.Inc()
                        TelemetryScope.logInfo "Thumbnail extracted successfully" scope
                        return true

            with
            | ex ->
                thumbnailFailureCounter.Inc()
                TelemetryScope.logError (Some ex) "Error during thumbnail extraction" scope
                return false
        }
    )
