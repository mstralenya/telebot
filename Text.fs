module Telebot.Text

open System.Text.RegularExpressions
open YoutubeExplode.Common

/// Represents a video file with optional metadata.
type VideoFile = {
    File: string
    Caption: string option
    Resolution: Resolution option
    DurationSeconds: int64 option
    Thumbnail: string option
}

/// Represents a reply that can be either a video file or a text message.
type Reply =
    | VideoFile of VideoFile
    | Message of string

module Reply =
    /// Creates a VideoFile reply.
    let createVideoFileWithThumbnail file caption resolution durationSeconds thumbnail=
        VideoFile {
            File = file
            Caption = caption
            Resolution = resolution
            DurationSeconds = durationSeconds
            Thumbnail = thumbnail
        }
    
    let createVideoFileWithCaption file caption = createVideoFileWithThumbnail file caption None None None
    
    let createVideoFile file= createVideoFileWithCaption file None

    /// Creates a Message reply.
    let createMessage text = Message text

let getLinks (regex: string) (text: string option) =
    text
    |> Option.map (fun text ->
        Regex.Matches(text, regex)
        |> Seq.cast<Match>
        |> Seq.map (_.Value)
        |> Seq.toList)
    |> Option.defaultValue List.empty