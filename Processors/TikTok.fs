module Telebot.TikTok

open System.Net
open System.Text.RegularExpressions
open Newtonsoft.Json.Linq
open Telebot.Bus
open Telebot.Handlers
open Telebot.Messages
open Telebot.Text
open Telebot.Text.Reply
open Telebot.VideoDownloader
open Wolverine.Attributes

module private TikTok =
    let private getJsonToken (json: JObject) token =
        json.SelectToken token
        |> Option.ofObj
        |> Option.map _.ToString()
        |> Option.defaultValue ""

    let private fetchTikTokUrl url =
        let response = HttpClient.getAsync $"https://www.tikwm.com/api/?url={url}?hd=1"

        match response.StatusCode with
        | HttpStatusCode.OK ->
            response.Content.ReadAsStringAsync()
            |> Async.AwaitTask
            |> Async.RunSynchronously
            |> JObject.Parse
            |> Some
        | _ -> None

    let private tikTokRegex =
        Regex(@"http(s)?://(www\.)?(\w+\.)?tiktok.com/(.*)", RegexOptions.Compiled)

    let getTikTokAudioLinks (message: string option) =
        match message with
        | Some text when text.Contains "audio" -> getLinks tikTokRegex message
        | _ -> List.empty

    let getTikTokVideoLinks (message: string option) =
        match message with
        | Some text when not (text.Contains "audio") -> getLinks tikTokRegex message
        | _ -> List.empty

    let private getAudioReply url =
        let apiResponse = fetchTikTokUrl url

        match apiResponse with
        | Some jObject ->
            let audioUrl = getJsonToken jObject "data.music"

            let fileName =
                getJsonToken jObject "data.music_info.title" |> fun title -> $"tt_{title}.mp3"

            downloadFileAsync audioUrl fileName |> Async.RunSynchronously

            Some(createAudioFile fileName)
        | None -> Some(createMessage "Failed to download tiktok audio")

    let private getVideoReply url =
        let apiResponse = fetchTikTokUrl url

        match apiResponse with
        | Some jObject ->
            let videoUrl = getJsonToken jObject "data.play"

            let fileName =
                getJsonToken jObject "data.id" |> fun title -> $"tt_{title}.mp4"

            downloadFileAsync videoUrl fileName |> Async.RunSynchronously

            Some(createVideoFile fileName)
        | None -> Some(createMessage "Failed to download tiktok video")

    let getTikTokReply (isVideo: bool) (url: string) =
        match isVideo with
        | true -> getVideoReply url
        | false -> getAudioReply url

type TikTokAudioLinksHandler() =
    inherit BaseHandler()
    member private this.extractTikTokAudioLinks =
        createLinkExtractor TikTok.getTikTokAudioLinks TikTokAudioMessage
    member private this.extractTikTokVideoLinks =
        createLinkExtractor TikTok.getTikTokVideoLinks TikTokVideoMessage
    [<WolverineHandler>]
    member this.HandleAudioLinks(msg: UpdateMessage) =
        this.extractTikTokAudioLinks msg |> List.map publishToBus |> ignore
    [<WolverineHandler>]
    member this.HandleVideoLinks(msg: UpdateMessage) =
        this.extractTikTokVideoLinks msg |> List.map publishToBus |> ignore
    member this.Handle(msg: TikTokAudioMessage) =
        this.processLink msg (TikTok.getTikTokReply false)    
    member this.Handle(msg: TikTokVideoMessage) =
        this.processLink msg (TikTok.getTikTokReply true)

