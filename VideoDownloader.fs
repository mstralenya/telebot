module Telebot.VideoDownloader

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Telebot.PrometheusMetrics
open Telebot.DataTypes
open Telebot.TelemetryService

// Async file download with telemetry and resource management
let downloadFileAsync (url: string) (filePath: string) : Async<unit> =
    withOperationTelemetry "file_download" (fun scope ->
        async {
            try
                TelemetryScope.addProperty "url" url scope |> ignore
                TelemetryScope.addProperty "file_path" filePath scope |> ignore
                TelemetryScope.logInfo $"Starting download from {url} to {filePath}" scope

                let! response = HttpClient.getAsync url
                response.EnsureSuccessStatusCode() |> ignore

                let! content = response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask

                // Write file asynchronously
                do! File.WriteAllBytesAsync(filePath, content) |> Async.AwaitTask

                // Record metrics
                downloadCounter.Inc()
                let fileSize = content.Length |> float
                let fileExtension = Path.GetExtension(filePath).TrimStart('.')
                fileSizeHistogram.WithLabels([|fileExtension|]).Observe(fileSize)

                TelemetryScope.addProperty "file_size" fileSize scope |> ignore
                TelemetryScope.logInfo $"Download completed successfully, file size: {fileSize} bytes" scope

            with
            | ex ->
                TelemetryScope.logError (Some ex) $"Failed to download file from {url}" scope
                raise ex
        }
    )

// Download media with better async handling
let downloadMediaAsync (url: string) (isVideo: bool) : Async<GalleryDisplay> =
    withOperationTelemetry "media_download" (fun scope ->
        async {
            let name = Guid.NewGuid()
            let extension = if isVideo then "mp4" else "jpg"
            let fileName = $"{name}.{extension}"

            TelemetryScope.addProperty "is_video" isVideo scope |> ignore
            TelemetryScope.addProperty "file_name" fileName scope |> ignore
            let mediaType = if isVideo then "video" else "image"
            TelemetryScope.logInfo (sprintf "Downloading %s media" mediaType) scope

            do! downloadFileAsync url fileName

            return if isVideo then Video fileName else Photo fileName
        }
    )

// Backward compatibility synchronous version
let downloadMedia url isVideo =
    downloadMediaAsync url isVideo |> Async.RunSynchronously

// Async file deletion with telemetry
let deleteFileAsync (filePath: string) : Async<unit> =
    withOperationTelemetry "file_delete" (fun scope ->
        async {
            try
                TelemetryScope.addProperty "file_path" filePath scope |> ignore

                if File.Exists filePath then
                    do! Task.Run(fun () -> File.Delete filePath) |> Async.AwaitTask
                    deleteCounter.Inc()
                    TelemetryScope.logInfo $"File deleted successfully: {filePath}" scope
                else
                    TelemetryScope.logWarning $"File not found for deletion: {filePath}" scope
            with
            | ex ->
                TelemetryScope.logError (Some ex) $"Failed to delete file: {filePath}" scope
                raise ex
        }
    )

// Backward compatibility synchronous version
let deleteFile (filePath: string) =
    deleteFileAsync filePath |> Async.RunSynchronously

// Get video info asynchronously with better error handling
let getVideoInfoAsync (videoPath: string) : Async<(int64 * int64 * int64) option> =
    withOperationTelemetry "video_info_extraction" (fun scope ->
        async {
            try
                TelemetryScope.addProperty "video_path" videoPath scope |> ignore
                TelemetryScope.logInfo $"Extracting video info from {videoPath}" scope

                let ffmpegPath = "ffprobe"
                let arguments =
                    sprintf "-v error -select_streams v:0 -show_entries format=duration -show_entries stream=width,height -of default=noprint_wrappers=1:nokey=1 \"%s\"" videoPath

                let startInfo =
                    ProcessStartInfo(
                        FileName = ffmpegPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    )

                use videoInfoProcess = new Process(StartInfo = startInfo)

                let! startResult = Task.Run(fun () -> videoInfoProcess.Start()) |> Async.AwaitTask
                if not startResult then
                    raise (InvalidOperationException("Failed to start ffprobe process"))

                let! output = videoInfoProcess.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
                let! error = videoInfoProcess.StandardError.ReadToEndAsync() |> Async.AwaitTask

                do! Task.Run(fun () -> videoInfoProcess.WaitForExit()) |> Async.AwaitTask

                if videoInfoProcess.ExitCode = 0 then
                    let matches =
                        System.Text.RegularExpressions.Regex.Match(output, @"(\d+)\n(\d+)\n(\d+)")

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
                    TelemetryScope.logError None $"ffprobe failed with exit code {videoInfoProcess.ExitCode}: {error}" scope
                    return None
            with
            | ex ->
                videoSizeFailureCounter.Inc()
                TelemetryScope.logError (Some ex) "Error extracting video info" scope
                return None
        }
    )

// Backward compatibility synchronous version
let getVideoInfo (videoPath: string) =
    getVideoInfoAsync videoPath |> Async.RunSynchronously

// Extract thumbnail asynchronously with better resource management
let extractThumbnailAsync (videoPath: string) (outputPath: string) : Async<bool> =
    withOperationTelemetry "thumbnail_extraction" (fun scope ->
        async {
            try
                TelemetryScope.addProperty "video_path" videoPath scope |> ignore
                TelemetryScope.addProperty "output_path" outputPath scope |> ignore
                TelemetryScope.logInfo $"Extracting thumbnail from {videoPath} to {outputPath}" scope

                let ffmpegPath = "ffmpeg"
                let arguments =
                    sprintf """ -y -i "%s" -vf "blackframe=0,metadata=select:key=lavfi.blackframe.pblack:value=90:function=less,scale='if(gt(iw,ih),320,-1)':'if(gt(ih,iw),320,-1)'" -frames:v 1 -q:v 2 -update 1 "%s" """ videoPath outputPath

                let startInfo =
                    ProcessStartInfo(
                        FileName = ffmpegPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    )

                use thumbnailProcess = new Process(StartInfo = startInfo)

                let! startResult = Task.Run(fun () -> thumbnailProcess.Start()) |> Async.AwaitTask
                if not startResult then
                    raise (InvalidOperationException("Failed to start ffmpeg process"))

                // Read streams asynchronously without blocking
                let readStreamAsync (streamReader: StreamReader) =
                    async {
                        let lines = System.Collections.Generic.List<string>()
                        let mutable line = ""
                        while not streamReader.EndOfStream do
                            let! currentLine = streamReader.ReadLineAsync() |> Async.AwaitTask
                            line <- currentLine
                            lines.Add(line)
                        return lines |> List.ofSeq
                    }

                let! outputTask = readStreamAsync thumbnailProcess.StandardOutput |> Async.StartChild
                let! errorTask = readStreamAsync thumbnailProcess.StandardError |> Async.StartChild

                do! Task.Run(fun () -> thumbnailProcess.WaitForExit()) |> Async.AwaitTask

                let! outputLines = outputTask
                let! errorLines = errorTask

                if thumbnailProcess.ExitCode <> 0 then
                    thumbnailFailureCounter.Inc()
                    let errorOutput = String.Join("\n", errorLines)
                    TelemetryScope.logError None $"Thumbnail extraction failed: {errorOutput}" scope
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

// Backward compatibility synchronous version
let extractThumbnail (videoPath: string) (outputPath: string) =
    extractThumbnailAsync videoPath outputPath |> Async.RunSynchronously |> ignore

// Utility functions
let getThumbnailName (videoPath: string) = $"{videoPath}.jpg"

// Get video thumbnail asynchronously
let getVideoThumbnailAsync (videoPath: string) : Async<string option> =
    async {
        let thumbnailFilename = getThumbnailName videoPath
        let! success = extractThumbnailAsync videoPath thumbnailFilename
        return if success then Some thumbnailFilename else None
    }

// Backward compatibility synchronous version
let getVideoThumbnail (videoPath: string) =
    let thumbnailFilename = getThumbnailName videoPath
    extractThumbnail videoPath thumbnailFilename
    thumbnailFilename

// Get video size asynchronously
let getVideoSizeAsync (filePath: string) : Async<int64 option * int64 option * int64 option> =
    async {
        let! info = getVideoInfoAsync filePath

        return match info with
                | Some (d, w, h) -> Some d, Some w, Some h
                | None -> None, None, None
    }

// Backward compatibility synchronous version
let getVideoSize (filePath: string) =
    getVideoSizeAsync filePath |> Async.RunSynchronously

// Clean up temporary files asynchronously
let cleanupTemporaryFilesAsync (files: string list) : Async<unit> =
    withOperationTelemetry "cleanup_temp_files" (fun scope ->
        async {
            TelemetryScope.addProperty "file_count" files.Length scope |> ignore
            TelemetryScope.logInfo $"Cleaning up {files.Length} temporary files" scope

            let! results =
                files
                |> List.map (fun file ->
                    async {
                        try
                            do! deleteFileAsync file
                            return Ok file
                        with
                        | ex -> return Error (file, ex)
                    })
                |> Async.Parallel

            let successes = results |> Array.choose (function Ok f -> Some f | Error _ -> None)
            let failures = results |> Array.choose (function Error (f, ex) -> Some (f, ex) | Ok _ -> None)

            TelemetryScope.addProperty "successful_deletions" successes.Length scope |> ignore
            TelemetryScope.addProperty "failed_deletions" failures.Length scope |> ignore

            if failures.Length > 0 then
                TelemetryScope.logWarning $"Failed to delete {failures.Length} files" scope
            else
                TelemetryScope.logInfo "All temporary files cleaned up successfully" scope
        }
    )
