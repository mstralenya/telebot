module Telebot.DataTypes

open Funogram.Telegram.Types

/// Represents a video file with optional metadata.
type VideoFile =
    {
        File: string
        Caption: string option
        ReplyMarkup: Markup option
    }

type GalleryDisplay =
    | Photo of string
    | Video of string

type Gallery =
    {
        Media: GalleryDisplay list
        Caption: string option
        ReplyMarkup: Markup option
    }

/// Represents a reply that can be either a video file or a text message.
type Reply =
    | VideoFile of VideoFile
    | Gallery of Gallery
    | Message of string * Markup option
    | AudioFile of string

type DownloadResult =
    | Success of Reply
    | InvalidUrl
    | DownloadError of string
