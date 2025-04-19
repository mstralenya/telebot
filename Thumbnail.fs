module Telebot.Thumbnail

open System
open System.Diagnostics
open System.IO
open Serilog

let getVideoInfo (videoPath: string) =
    let ffmpegPath = "ffprobe" // or full path to ffmpeg.exe if not in PATH

    let arguments = $"-i \"{videoPath}\" -hide_banner"

    let startInfo = ProcessStartInfo(
        FileName = ffmpegPath,
        Arguments = arguments,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    )

    use thumbnailProcess = new Process(StartInfo = startInfo)

    thumbnailProcess.Start() |> ignore

    let error = thumbnailProcess.StandardError.ReadToEnd() // FFmpeg outputs info to stderr

    thumbnailProcess.WaitForExit()

    if thumbnailProcess.ExitCode = 0 then
        // Parse duration and resolution from FFmpeg output
        let durationMatch = System.Text.RegularExpressions.Regex.Match(error, @"Duration: (\d+):(\d+):(\d+).(\d+)")
        let resolutionMatch = System.Text.RegularExpressions.Regex.Match(error, @"(\d{2,})x(\d{2,})")

        if durationMatch.Success && resolutionMatch.Success then
            let hours = int durationMatch.Groups.[1].Value
            let minutes = int durationMatch.Groups.[2].Value
            let seconds = int durationMatch.Groups.[3].Value
            let milliseconds = int durationMatch.Groups.[4].Value

            let durationInSeconds = int64 (hours * 3600 + minutes * 60 + seconds + (milliseconds / 1000))

            let width = int64 resolutionMatch.Groups.[1].Value
            let height = int64 resolutionMatch.Groups.[2].Value

            Log.Information $"Video Info: Duration: {durationInSeconds} seconds, Resolution: {width}x{height}"
            
            Some (durationInSeconds, width, height)
        else
            None
    else
        Log.Error($"Error: {error}")
        None

let extractThumbnail (videoPath: string) (outputPath: string) =
    // Ensure FFmpeg is in your PATH or specify the full path to ffmpeg.exe
    let ffmpegPath = "ffmpeg" // or full path like "C:\\path\\to\\ffmpeg.exe"

    let arguments = $""" -y -i "{videoPath}"  -vf "blackframe=0,metadata=select:key=lavfi.blackframe.pblack:value=90:function=less,scale='if(gt(iw,ih),320,-1)':'if(gt(ih,iw),320,-1)'" -frames:v 1 -q:v 2 "{outputPath}" """

    let startInfo = ProcessStartInfo(
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
    let readStreamAsync (streamReader: StreamReader) = async {
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
        Log.Error($"Error extracting thumbnail: {errorTask.Result}")
    else
        Log.Information($"Thumbnail extracted successfully: {outputPath}")
        
let getVideoThumbnail (videoPath: string) =
    let thumbnailFilename = $"{Guid.NewGuid()}.jpg"
    extractThumbnail videoPath thumbnailFilename
    thumbnailFilename