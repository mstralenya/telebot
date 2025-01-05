module Telebot.Text

open System.Text.RegularExpressions
open YoutubeExplode.Common

/// Represents a video file with optional metadata.
type VideoFile = {
    File: string
    Caption: string option
}

/// Represents a reply that can be either a video file or a text message.
type Reply =
    | VideoFile of VideoFile
    | Message of string

module Reply =
    let createVideoFileWithCaption file caption =
        VideoFile {
            File = file
            Caption = caption
        }
    
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