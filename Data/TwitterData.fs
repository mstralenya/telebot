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
        | "video"
        | "gif" -> TwitterMedia.Video
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

type TwitterTranslation =
    {
        text: string
        source_language: string
        destination_language: string
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
        translation: TwitterTranslation option
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
        translation: TwitterTranslation option
    }

type FxTranslation =
    {
        text: string
        source_lang: string
        target_lang: string
    }

type FxAuthor =
    {
        name: string
        screen_name: string
        avatar_url: string option
    }

type FxMediaItem =
    {
        [<JsonPropertyName("type")>]
        mediaType: string
        url: string
        width: int
        height: int
        thumbnail_url: string option
        altText: string option
    }

type FxMedia =
    {
        all: FxMediaItem list option
    }

type FxTweet =
    {
        id: string
        url: string
        text: string option
        author: FxAuthor
        media: FxMedia option
        translation: FxTranslation option
        community_note: string option
        created_at: string option
        created_timestamp: int64
        quote: FxTweet option
    }

type FxTweetResponse =
    {
        code: int
        message: string
        tweet: FxTweet
    }

module FxConverter =
    let private mapMediaItem (item: FxMediaItem) : TwitterMediaExtended =
        let mediaType =
            match item.mediaType.ToLowerInvariant() with
            | "video"
            | "gif" -> TwitterMedia.Video
            | _ -> TwitterMedia.Image
        {
            altText = item.altText
            size = { height = item.height; width = item.width }
            thumbnail_url = defaultArg item.thumbnail_url item.url
            mediaType = mediaType
            url = item.url
        }

    let private allSameType (mediaList: TwitterMediaExtended list) =
        match mediaList with
        | [] -> true
        | first :: rest -> rest |> List.forall (fun item -> item.mediaType = first.mediaType)

    let private mapTranslation (t: FxTranslation option) : TwitterTranslation option =
        match t with
        | Some tl ->
            Some {
                text = tl.text
                source_language = tl.source_lang
                destination_language = tl.target_lang
            }
        | None -> None

    let rec private toTwitterQrt (fxTweet: FxTweet) : TwitterQrt =
        let mediaList =
            fxTweet.media
            |> Option.bind (fun m -> m.all)
            |> Option.defaultValue []
            |> List.map mapMediaItem
        let mediaUrls = mediaList |> List.map (fun m -> m.url)
        {
            allSameType = allSameType mediaList
            combinedMediaUrl = None
            communityNote = fxTweet.community_note
            conversationID = fxTweet.id
            date = defaultArg fxTweet.created_at ""
            date_epoch = fxTweet.created_timestamp
            hasMedia = not mediaList.IsEmpty
            mediaURLs = mediaUrls
            media_extended = mediaList
            qrtURL = fxTweet.quote |> Option.map (fun q -> q.url) |> Option.defaultValue ""
            text = fxTweet.text
            tweetID = fxTweet.id
            tweetURL = fxTweet.url
            user_name = fxTweet.author.name
            user_profile_image_url = defaultArg fxTweet.author.avatar_url ""
            user_screen_name = fxTweet.author.screen_name
            translation = mapTranslation fxTweet.translation
        }

    let rec toTweet (fxTweet: FxTweet) : Tweet =
        let mediaList =
            fxTweet.media
            |> Option.bind (fun m -> m.all)
            |> Option.defaultValue []
            |> List.map mapMediaItem
        let mediaUrls = mediaList |> List.map (fun m -> m.url)
        {
            date_epoch = fxTweet.created_timestamp
            mediaURLs = mediaUrls
            media_extended = mediaList
            qrt = fxTweet.quote |> Option.map toTwitterQrt
            qrtURL = fxTweet.quote |> Option.map (fun q -> q.url) |> Option.defaultValue ""
            text = fxTweet.text
            tweetID = fxTweet.id
            tweetURL = fxTweet.url
            user_name = fxTweet.author.name
            user_profile_image_url = defaultArg fxTweet.author.avatar_url ""
            user_screen_name = fxTweet.author.screen_name
            translation = mapTranslation fxTweet.translation
        }
