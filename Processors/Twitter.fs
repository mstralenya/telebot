module Telebot.Twitter

open System.Linq
open System.Net.Http.Json
open System.Text.Json
open System.Text.RegularExpressions
open Serilog
open Telebot.Bus
open Telebot.Handlers
open Telebot.Messages
open Telebot.Text
open Telebot.TwitterData
open Telebot.VideoDownloader

module private Twitter =
    // Function to replace the domain in the URL and append translation language suffix if configured
    let private replaceDomain (url: string) =
        let envLang = System.Environment.GetEnvironmentVariable("TWITTER_TRANSLATION_LANG")
        let envApiBase = System.Environment.GetEnvironmentVariable("TWITTER_API_BASE")
        let vxString =
            if System.String.IsNullOrWhiteSpace(envApiBase) then
                "https://api.fxtwitter.com/"
            else
                let trimmed = envApiBase.Trim()
                if trimmed.EndsWith("/") then trimmed else trimmed + "/"

        let replaced =
            url.Replace("https://x.com/", vxString)
               .Replace("https://twitter.com/", vxString)
        
        if System.String.IsNullOrWhiteSpace(envLang) then
            replaced
        else
            let langCode = envLang.Trim()
            let trimmed = replaced.TrimEnd('/')
            $"{trimmed}/{langCode}"

    // Main function to process the URL and return the Tweet structure
    let private getTweetFromUrlAsync (url: string) =
        async {
            let newUrl = replaceDomain url
            let envLang = System.Environment.GetEnvironmentVariable("TWITTER_TRANSLATION_LANG")
            if not (System.String.IsNullOrWhiteSpace(envLang)) then
                Log.Information("Fetching Twitter URL {Url} with translation to {Lang}", url, envLang.Trim())
            else
                Log.Information("Fetching Twitter URL {Url} without translation", url)

            let useProxy = Telebot.HttpClient.ProxyConfig.useProxyForTwitter()
            let! response = HttpClient.getAsync newUrl useProxy
            let options = JsonSerializerOptions()
            options.PropertyNameCaseInsensitive <- true
            match response.StatusCode with
            | System.Net.HttpStatusCode.OK ->
                let! result = response.Content.ReadFromJsonAsync<FxTweetResponse> options |> Async.AwaitTask
                if result.code = 200 then
                    return Some (FxConverter.toTweet result.tweet)
                else
                    Log.Warning("FxTwitter API returned non-200 code {Code}: {Message}", result.code, result.message)
                    return None
            | _ ->
                Log.Warning("FxTwitter API request failed with status code {StatusCode}", response.StatusCode)
                return None
        }

    let private twitterRegex =
        Regex(@"https://(x|twitter).com/.*/status/(\d+)", RegexOptions.Compiled)

    // Function to process a list of URLs and return an array of results
    let private processUrlsAsync (urls: TwitterMediaExtended list) =
        async {
            let useProxy = Telebot.HttpClient.ProxyConfig.useProxyForTwitter()
            let! results =
                urls
                |> List.map (fun media -> downloadMediaAsync media.url (media.mediaType = TwitterMedia.Video) useProxy)
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
                let textToUse =
                    match tweet.translation with
                    | Some tl when not (System.String.IsNullOrWhiteSpace(tl.text)) ->
                        let direction = $"{tl.source_language.ToUpper()}→{tl.destination_language.ToUpper()}"
                        Log.Information("Successfully retrieved translation for tweet {TweetId} ({Direction})", tweet.tweetID, direction)
                        Some $"<i>【TL {direction}】</i>\n{tl.text}"
                    | _ -> tweet.text

                let qrtTextToUse =
                    match tweet.qrt with
                    | Some qrt ->
                        match qrt.translation with
                        | Some tl when not (System.String.IsNullOrWhiteSpace(tl.text)) ->
                            let direction = $"{tl.source_language.ToUpper()}→{tl.destination_language.ToUpper()}"
                            Log.Information("Successfully retrieved translation for quoted tweet {TweetId} ({Direction})", qrt.tweetID, direction)
                            Some $"<i>【TL {direction}】</i>\n{tl.text}"
                        | _ -> qrt.text
                    | None -> None

                let replyText =
                    match tweet.user_screen_name, tweet.user_name, textToUse, tweet.qrt with
                    | ah,
                      a,
                      Some t,
                      Some qrt ->
                        match qrtTextToUse with
                        | Some qtxt ->
                            let qa = qrt.user_name
                            let qah = qrt.user_screen_name
                            Some
                                $"""<b>{a}</b> <i>(@​{ah})</i>:<blockquote>{t}</blockquote>Quoting <b>{qa}</b><i>(@​{qah})</i>:<blockquote>{qtxt}</blockquote>"""
                        | None ->
                            Some $"<b>{a}</b> <i>(@​{ah})</i>: <blockquote>{t}</blockquote>"
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
