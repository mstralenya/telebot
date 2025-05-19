module Telebot.TwitterData

open System.Text.Json.Serialization

// Define the data structures
type TwitterSize = { height: int; width: int }

type TwitterMedia =
    | Image = 0
    | Video = 1

type TwitterMediaJsonConverter() =
    inherit JsonConverter<TwitterMedia>()

    let parseMediaType (mediaStr: string) =
        match mediaStr.ToLowerInvariant() with
        | "video" | "gif" -> TwitterMedia.Video
        | "image" -> TwitterMedia.Image
        | _ -> failwith $"Unknown media type: {mediaStr}"

    override _.Read(reader, _typeToConvert, options) =
        let mediaType = reader.GetString()
        parseMediaType mediaType

    override _.Write(writer, value, options) =
        match value with
        | TwitterMedia.Image -> writer.WriteStringValue "image"
        | TwitterMedia.Video -> writer.WriteStringValue "video"
        | _ -> System.ArgumentOutOfRangeException() |> raise

type TwitterMediaExtended =
    {
        altText: string option
        size: TwitterSize
        thumbnail_url: string
        [<JsonPropertyName("type")>]
        [<JsonConverter(typeof<TwitterMediaJsonConverter>)>]
        mediaType: TwitterMedia
        url: string
    }

type TwitterQrt =
    {
        allSameType: bool
        combinedMediaUrl: string option
        communityNote: string option
        conversationID: string
        date: string
        date_epoch: int64
        hasMedia: bool
        mediaURLs: string list
        media_extended: TwitterMediaExtended list
        qrtURL: string
        text: string option
        tweetID: string
        tweetURL: string
        user_name: string
        user_profile_image_url: string
        user_screen_name: string
    }

type Tweet =
    {
        date_epoch: int64
        mediaURLs: string list
        media_extended: TwitterMediaExtended list
        qrt: TwitterQrt option
        qrtURL: string
        text: string option
        tweetID: string
        tweetURL: string
        user_name: string
        user_profile_image_url: string
        user_screen_name: string
    }
