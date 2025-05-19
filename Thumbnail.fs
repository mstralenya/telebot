module Telebot.Thumbnail

open System.Diagnostics
open System.IO
open Serilog
open Telebot.PrometheusMetrics

let getVideoInfo (videoPath: string) =
    let ffmpegPath = "ffprobe" // or full path to ffmpeg.exe if not in PATH

    let arguments =
        $"-v error -select_streams v:0 -show_entries format=duration -show_entries stream=width,height \
-of default=noprint_wrappers=1:nokey=1 \"{videoPath}\""

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

    thumbnailProcess.Start() |> ignore

    let output = thumbnailProcess.StandardOutput.ReadToEnd()
    let error = thumbnailProcess.StandardError.ReadToEnd() // FFmpeg outputs info to stderr

    thumbnailProcess.WaitForExit()

    if thumbnailProcess.ExitCode = 0 then
        // Parse duration and resolution from FFmpeg output
        let matches =
            System.Text.RegularExpressions.Regex.Match(output, @"(\d+)\n(\d+)\n(\d+)")

        if matches.Success then
            let results = 
                [1..3] |> List.map (fun i -> int64 matches.Groups[i].Value)

            Log.Information $"Video Info: Duration: {results[2]} seconds, Resolution: {results[1]}x{results[0]}"
            videoSizeSuccessCounter.Inc()
            Some(results[2], results[0], results[1])
        else
            videoSizeFailureCounter.Inc()
            None
    else
        videoSizeFailureCounter.Inc()
        Log.Error $"Error: {error}"
        None

let extractThumbnail (videoPath: string) (outputPath: string) =
    // Ensure FFmpeg is in your PATH or specify the full path to ffmpeg.exe
    let ffmpegPath = "ffmpeg" // or full path like "C:\\path\\to\\ffmpeg.exe"

    let arguments =
        $""" -y -i "{videoPath}"  -vf "blackframe=0,metadata=select:key=lavfi.blackframe.pblack:value=90:function=less,scale='if(gt(iw,ih),320,-1)':'if(gt(ih,iw),320,-1)'" -frames:v 1 -q:v 2 -update 1 "{outputPath}" """

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

    thumbnailProcess.Start() |> ignore

    // Read output and error streams in separate tasks to avoid blocking
    let readStreamAsync (streamReader: StreamReader) =
        async {
            let mutable line = ""

            while not streamReader.EndOfStream do
                line <- streamReader.ReadLine()
                Log.Information line
        }

    let outputTask = Async.StartAsTask(readStreamAsync thumbnailProcess.StandardOutput)
    let errorTask = Async.StartAsTask(readStreamAsync thumbnailProcess.StandardError)

    thumbnailProcess.WaitForExit()

    // Wait for reading tasks to complete
    outputTask.Wait()
    errorTask.Wait()

    if thumbnailProcess.ExitCode <> 0 then
        thumbnailFailureCounter.Inc()
        Log.Error $"Error extracting thumbnail: {errorTask.Result}"
    else
        thumbnailSuccessCounter.Inc()
        Log.Information $"Thumbnail extracted successfully: {outputPath}"

let getThumbnailName (videoPath: string) = $"{videoPath}.jpg"

let getVideoThumbnail (videoPath: string) =
    let thumbnailFilename = getThumbnailName videoPath
    extractThumbnail videoPath thumbnailFilename
    thumbnailFilename
