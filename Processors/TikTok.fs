module Telebot.TikTok

open System
open System.Net
open System.Text.RegularExpressions
open Newtonsoft.Json.Linq
open Serilog
open Telebot.Bus
open Telebot.Handlers
open Telebot.PrometheusMetrics
open Telebot.Messages
open Telebot.Text
open Telebot.Text.Reply
open Telebot.VideoDownloader
open Wolverine.Attributes

module TikTok =
    let private getJsonToken (json: JObject) token =
        json.SelectToken token
        |> Option.ofObj
        |> Option.map _.ToString()
        |> Option.defaultValue ""

    let private fetchTikTokUrl url =
        async {
            let useProxy = Telebot.HttpClient.ProxyConfig.useProxyForTikTok()
            let! response = HttpClient.getAsync $"https://www.tikwm.com/api/?url={url}?hd=1" useProxy

            match response.StatusCode with
            | HttpStatusCode.OK ->
                let! content = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                return content |> JObject.Parse |> Some
            | _ -> return None
        }

    let tikTokRegex =
        Regex(@"http(s)?://(www\.)?(\w+\.)?tiktok.com/(.*)", RegexOptions.Compiled)

    let getTikTokAudioLinks (message: string option) =
        match message with
        | Some text when text.IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0 -> getLinks tikTokRegex message
        | _ -> List.empty

    let getTikTokVideoLinks (message: string option) =
        match message with
        | Some text when text.IndexOf("audio", StringComparison.OrdinalIgnoreCase) < 0 -> getLinks tikTokRegex message
        | _ -> List.empty

    let private getAudioReply url =
        async {
            try
                let! apiResponse = fetchTikTokUrl url

                match apiResponse with
                | Some jObject ->
                    let audioUrl = getJsonToken jObject "data.music"
                    if String.IsNullOrWhiteSpace(audioUrl) then
                        tiktokFailureMetric.Inc()
                        return Some(createMessage "Failed to download tiktok audio")
                    else
                        let fileName = $"tt_{Guid.NewGuid()}.mp3"
                        let useProxy = Telebot.HttpClient.ProxyConfig.useProxyForTikTok()
                        do! downloadFileAsync audioUrl fileName useProxy
                        tiktokSuccessMetric.Inc()
                        return Some(createAudioFile fileName)
                | None ->
                    tiktokFailureMetric.Inc()
                    return Some(createMessage "Failed to download tiktok audio")
            with ex ->
                Log.Error(ex, "Error processing TikTok audio")
                tiktokFailureMetric.Inc()
                return None
        }

    let private getVideoReply url =
        async {
            try
                let! apiResponse = fetchTikTokUrl url

                match apiResponse with
                | Some jObject ->
                    let videoUrl = getJsonToken jObject "data.play"
                    if String.IsNullOrWhiteSpace(videoUrl) then
                        tiktokFailureMetric.Inc()
                        return Some(createMessage "Failed to download tiktok video")
                    else
                        let fileName = $"tt_{Guid.NewGuid()}.mp4"
                        let useProxy = Telebot.HttpClient.ProxyConfig.useProxyForTikTok()
                        do! downloadFileAsync videoUrl fileName useProxy
                        tiktokSuccessMetric.Inc()
                        return Some(createVideoFile fileName)
                | None ->
                    tiktokFailureMetric.Inc()
                    return Some(createMessage "Failed to download tiktok video")
            with ex ->
                Log.Error(ex, "Error processing TikTok video")
                tiktokFailureMetric.Inc()
                return None
        }

    let getTikTokReply (isVideo: bool) (url: string) =
        async {
            match isVideo with
            | true -> return! getVideoReply url
            | false -> return! getAudioReply url
        }

type TikTokAudioLinksHandler() =
    inherit BaseHandler()
    member private this.extractTikTokAudioLinks =
        createLinkExtractor TikTok.getTikTokAudioLinks TikTokAudioMessage
    member private this.extractTikTokVideoLinks =
        createLinkExtractor TikTok.getTikTokVideoLinks TikTokVideoMessage
    [<WolverineHandler>]
    member this.HandleAudioLinks(msg: UpdateMessage) =
        this.extractTikTokAudioLinks msg |> List.map (publishToBusAsync >> Async.RunSynchronously) |> ignore
    [<WolverineHandler>]
    member this.HandleVideoLinks(msg: UpdateMessage) =
        this.extractTikTokVideoLinks msg |> List.map (publishToBusAsync >> Async.RunSynchronously) |> ignore
    member this.Handle(msg: TikTokAudioMessage) =
        this.processLink msg (TikTok.getTikTokReply false >> Async.RunSynchronously)    
    member this.Handle(msg: TikTokVideoMessage) =
        this.processLink msg (TikTok.getTikTokReply true >> Async.RunSynchronously)

