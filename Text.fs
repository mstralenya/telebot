module Telebot.Text

open System.Text.RegularExpressions

/// Represents a video file with optional metadata.
type VideoFile = {
    File: string
    Caption: string option
}

type GalleryDisplay =
    | Photo of string
    | Video of string

type Gallery = {
    Media: GalleryDisplay list
    Caption: string option
}

/// Represents a reply that can be either a video file or a text message.
type Reply =
    | VideoFile of VideoFile
    | Gallery of Gallery
    | Message of string

type DownloadResult =
    | Success of Reply
    | InvalidUrl
    | DownloadError of string

module Reply =
    let createVideoFileWithCaption file caption =
        VideoFile {
            File = file
            Caption = caption
        }
    
    let createVideoFile file= createVideoFileWithCaption file None
    
    let createGallery files caption = Gallery {
        Media = files
        Caption = caption
    }

    /// Creates a Message reply.
    let createMessage text = Message text

let getLinks (regex: Regex) (text: string option) =
    text
    |> Option.map (fun text ->
        regex.Matches(text)
        |> Seq.cast<Match>
        |> Seq.map (_.Value)
        |> Seq.toList)
    |> Option.defaultValue List.empty

let truncateWithEllipsis (input: string option) (maxLength: int) : string option =
    match input with
    | Some str ->
        if str.Length <= maxLength then
            Some str
        else
            let truncated = str.Substring(0, maxLength - 3)
            Some (truncated + "...")
    | None -> None