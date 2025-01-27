module Telebot.Text

open System.Text.RegularExpressions
open YoutubeExplode.Common

/// Represents a video file with optional metadata.
type VideoFile = {
    File: string
    Caption: string option
}

type GalleryDisplay =
    | Photo of string
    | Video of string

type ImageGallery = {
    Photos: GalleryDisplay list
    Caption: string option
}

/// Represents a reply that can be either a video file or a text message.
type Reply =
    | VideoFile of VideoFile
    | ImageGallery of ImageGallery
    | Message of string

module Reply =
    let createVideoFileWithCaption file caption =
        VideoFile {
            File = file
            Caption = caption
        }
    
    let createVideoFile file= createVideoFileWithCaption file None
    
    let createImageGallery files caption = ImageGallery {
        Photos = files
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
