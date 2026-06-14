module Telebot.Twitter

open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open Telebot.Bus
open Telebot.Handlers
open Telebot.LoggingHandler
open Telebot.Messages
open Telebot.Text
open Telebot.TwitterData
open Telebot.VideoDownloader

module private Twitter =
    // Function to replace the domain in the URL
    let private replaceDomain (url: string) =
        url.Replace("https://x.com/", "https://api.vxtwitter.com/")
           .Replace("https://twitter.com/", "https://api.vxtwitter.com/")

    // Main function to process the URL and return the Tweet structure
    let private getTweetFromUrlAsync (url: string) =
        async {
            let newUrl = replaceDomain url
            let useProxy = Telebot.HttpClient.ProxyConfig.useProxyForTwitter()
            let! response = HttpClient.getAsync newUrl useProxy
            let options = JsonSerializerOptions()
            options.PropertyNameCaseInsensitive <- true
            match response.StatusCode with
            | System.Net.HttpStatusCode.OK -> let! result = response.Content.ReadFromJsonAsync<Tweet> options |> Async.AwaitTask
                                              return result |> Some
            | _ -> return None
        }

    let private twitterRegex =
        Regex(@"https://(x|twitter).com/.*/status/(\d+)", RegexOptions.Compiled)

    let private formatMedia (mediaList: TwitterMediaExtended list) =
        if List.isEmpty mediaList then
            ""
        else
            let sb = System.Text.StringBuilder()
            sb.Append("<tg-collage>") |> ignore
            for media in mediaList do
                match media.mediaType with
                | TwitterMedia.Video ->
                    sb.AppendFormat("<video src=\"{0}\"/>", media.url) |> ignore
                | _ ->
                    sb.AppendFormat("<img src=\"{0}\"/>", media.url) |> ignore
            sb.Append("</tg-collage>") |> ignore
            sb.ToString()

    let getTwitterReplyAsync (url: string) =
        async {
            let! tweet = getTweetFromUrlAsync url
            match tweet with
            | None -> return None
            | Some tweet ->
                let mainText =
                    match tweet.user_screen_name, tweet.user_name, tweet.text with
                    | ah, a, Some t -> $"<b>{a}</b> <i>(@​{ah})</i>: <blockquote>{t}</blockquote>"
                    | ah, a, _ -> $"<b>{a}</b> <i>(@​{ah})</i>:"

                let mainMedia = formatMedia tweet.media_extended

                let quotedContent =
                    match tweet.qrt with
                    | Some qrt ->
                        let qrtText =
                            match qrt.user_screen_name, qrt.user_name, qrt.text with
                            | qah, qa, Some qtxt -> $"Quoting <b>{qa}</b> <i>(@​{qah})</i>: <blockquote>{qtxt}</blockquote>"
                            | qah, qa, _ -> $"Quoting <b>{qa}</b> <i>(@​{qah})</i>:"
                        let qrtMedia = formatMedia qrt.media_extended
                        qrtText + qrtMedia
                    | None -> ""

                let html = mainText + mainMedia + quotedContent
                return Some (Reply.createRichMessage html)
        }

    let getTwitterReply (url: string) =
        getTwitterReplyAsync url |> Async.RunSynchronously

    let getTwitterLinks (message: string option) = getLinks twitterRegex message

type TwitterLinksHandler() =
    inherit BaseHandler()

    member private this.extractTwitterLinks =
        createLinkExtractor Twitter.getTwitterLinks TwitterMessage

    member this.Handle(msg: UpdateMessage) =
        this.extractTwitterLinks msg |> List.map (publishToBusAsync >> Async.RunSynchronously) |> ignore

    member this.Handle(msg: TwitterMessage) =
        this.processLink msg Twitter.getTwitterReply
