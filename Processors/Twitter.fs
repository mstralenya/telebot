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

    // Main function to process the URL and return the Tweet structure
    let private getTweetFromUrlAsync (url: string) =
        async {
            let newUrl = replaceDomain url
            let! response = HttpClient.getAsync newUrl
            let options = JsonSerializerOptions()
            options.PropertyNameCaseInsensitive <- true
            match response.StatusCode with
            | System.Net.HttpStatusCode.OK -> let! result = response.Content.ReadFromJsonAsync<Tweet> options |> Async.AwaitTask
                                              return result |> Some
            | _ -> return None
        }

    let private twitterRegex =
        Regex(@"https://(x|twitter).com/.*/status/(\d+)", RegexOptions.Compiled)

    // Function to process a list of URLs and return an array of results
    let private processUrlsAsync (urls: TwitterMediaExtended list) =
        async {
            let! results =
                urls
                |> List.map (fun media -> downloadMediaAsync media.url (media.mediaType = TwitterMedia.Video))
                |> Async.Parallel
            return results |> Array.toList
        }

    let private mergeMediaUrls (tweet: Tweet) =
        match tweet.qrt with
        | Some qrt -> tweet.media_extended @ qrt.media_extended // Concatenate the two lists if qrt is Some
        | None -> tweet.media_extended // If qrt is None, just return the main tweet's mediaURLs

    let getTwitterReplyAsync (url: string) =
        async {
            let! tweet = getTweetFromUrlAsync url
            match tweet with
            | None -> return None
            | Some tweet ->
                let replyText =
                    match tweet.user_screen_name, tweet.user_name, tweet.text, tweet.qrt with
                    | ah,
                      a,
                      Some t,
                      Some {
                               user_name = qa
                               user_screen_name = qah
                               text = Some qtxt
                           } ->
                        Some
                            $"""<b>{a}</b> <i>(@​{ah})</i>:<blockquote>{t}</blockquote>Quoting <b>{qa}</b><i>(@​{qah})</i>:<blockquote>{qtxt}</blockquote>"""
                    | ah, a, Some t, _ -> Some $"<b>{a}</b> <i>(@​{ah})</i>: <blockquote>{t}</blockquote>"
                    | ah, a, _, _ -> Some $"<b>{a}</b> <i>(@​{ah})</i>:"

                let mediaUrls = mergeMediaUrls tweet
                let! gallery = processUrlsAsync mediaUrls

                match gallery.Length with
                | i when i > 0 -> return Some(Reply.createGallery (List.toArray gallery) replyText)
                | _ -> return Some(Reply.createMessage replyText.Value)
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
