module Telebot.Twitter

open Funogram.Telegram.Types
open System.Linq
open System.Threading.Tasks
open System.Net.Http.Json
open System.Text.Json
open System.Text.RegularExpressions
open Serilog
open Telebot.Bus
open Telebot.Handlers
open Telebot.PrometheusMetrics
open Telebot.Messages
open Telebot.Replies
open Telebot.TwitterData
open Telebot.VideoDownloader

module Twitter =
    // Function to replace the domain in the URL and append translation language suffix if configured
    let private replaceDomain (url: string) =
        let config = Config.get ()
        let replaced =
            url.Replace("https://x.com/", config.TwitterApiBase)
               .Replace("https://twitter.com/", config.TwitterApiBase)

        match config.TwitterTranslationLang with
        | None -> replaced
        | Some langCode -> $"{replaced.TrimEnd('/')}/{langCode}"

    // Main function to process the URL and return the Tweet structure
    let private getTweetFromUrlAsync (url: string) =
        async {
            let newUrl = replaceDomain url
            match Config.get().TwitterTranslationLang with
            | Some lang -> Log.Information("Fetching Twitter URL {Url} with translation to {Lang}", url, lang)
            | None -> Log.Information("Fetching Twitter URL {Url} without translation", url)

            let useProxy = HttpClient.ProxyConfig.useProxyForTwitter()
            let! response = HttpClient.getAsync newUrl useProxy
            use _ = response
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

    let twitterRegex =
        Regex(@"https://(x|twitter).com/.*/status/(\d+)", RegexOptions.Compiled)

    // Function to process a list of URLs and return an array of results
    let private processUrlsAsync (urls: TwitterMediaExtended list) =
        async {
            let useProxy = HttpClient.ProxyConfig.useProxyForTwitter()
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

    // Formats a tweet (and optionally its quoted tweet) into the HTML message body
    let internal renderTweet (screenName: string) (userName: string) (body: string option) (qrt: TwitterQrt option) (qrtBody: string option) : string =
        match body with
        | Some t ->
            match qrt, qrtBody with
            | Some q, Some qtxt ->
                $"""<b>{userName}</b> <i>(@​{screenName})</i>:<blockquote>{t}</blockquote>Quoting <b>{q.user_name}</b><i>(@​{q.user_screen_name})</i>:<blockquote>{qtxt}</blockquote>"""
            | _ -> $"<b>{userName}</b> <i>(@​{screenName})</i>: <blockquote>{t}</blockquote>"
        | None -> $"<b>{userName}</b> <i>(@​{screenName})</i>:"

    let getTwitterReplyAsync (chatId: ChatId) (url: string) =
        async {
            try
                let! tweetOpt = getTweetFromUrlAsync url
                match tweetOpt with
                | None ->
                    twitterFailureCounter.Inc()
                    return None
                | Some tweet ->
                    let! tweet =
                        async {
                            let config = Config.get ()
                            let isLlmEnabled = Translation.getLlmApiUrl().IsSome
                            match config.TwitterTranslationLang with
                            | Some targetLang when isLlmEnabled ->
                                let! mainTl =
                                    match tweet.text with
                                    | Some txt when not (System.String.IsNullOrWhiteSpace(txt)) ->
                                        Translation.translateTextAsync txt targetLang
                                    | _ -> async { return None }
                                let! qrtTl =
                                    match tweet.qrt with
                                    | Some qrt ->
                                        match qrt.text with
                                        | Some txt when not (System.String.IsNullOrWhiteSpace(txt)) ->
                                            Translation.translateTextAsync txt targetLang
                                        | _ -> async { return None }
                                    | None -> async { return None }
                                
                                let updatedQrt =
                                    tweet.qrt
                                    |> Option.map (fun qrt ->
                                        match qrtTl with
                                        | Some tl -> { qrt with translation = Some tl }
                                        | None -> qrt
                                    )
                                
                                let updatedTweet =
                                    match mainTl with
                                    | Some tl -> { tweet with translation = Some tl; qrt = updatedQrt }
                                    | None -> { tweet with qrt = updatedQrt }
                                return updatedTweet
                            | _ -> return tweet
                        }

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
                        renderTweet tweet.user_screen_name tweet.user_name textToUse tweet.qrt qrtTextToUse
                        |> Some

                    let mediaUrls = mergeMediaUrls tweet
                    let! gallery = processUrlsAsync mediaUrls

                    twitterSuccessCounter.Inc()
                    let baseReply =
                        match gallery.Length with
                        | i when i > 0 -> Reply.createGallery (List.toArray gallery) replyText
                        | _ -> Reply.createMessage replyText.Value

                    // Check if translation was actually applied to add a "Show Original Text" button
                    let hasTranslation =
                        (tweet.translation |> Option.bind (fun tl -> if System.String.IsNullOrWhiteSpace(tl.text) then None else Some tl) |> Option.isSome) ||
                        (tweet.qrt |> Option.bind (fun q -> q.translation |> Option.bind (fun tl -> if System.String.IsNullOrWhiteSpace(tl.text) then None else Some tl)) |> Option.isSome)

                    if hasTranslation && replyText.IsSome then
                        let originalText =
                            renderTweet tweet.user_screen_name tweet.user_name tweet.text tweet.qrt (tweet.qrt |> Option.bind _.text)

                        let isGroupChat =
                            match chatId with
                            | ChatId.Int id -> id < 0L
                            | ChatId.String s -> s.StartsWith("-")

                        let cacheId = Translation.saveTranslationToCache originalText replyText.Value
                        let btnToggle = InlineKeyboardButton.Create("Show Original Text", callbackData = $"show_orig:{cacheId}")

                        let buttons =
                            match Config.get().WebAppBaseUrl with
                            | Some webAppBase when not isGroupChat ->
                                let url = $"{webAppBase.TrimEnd('/')}/webapp?id={cacheId}"
                                let btnWebApp = InlineKeyboardButton.Create("Original (Web)", webApp = WebAppInfo.Create(url))
                                [| [| btnWebApp; btnToggle |] |]
                            | _ ->
                                [| [| btnToggle |] |]

                        let keyboard = InlineKeyboardMarkup.Create(buttons)
                        let replyMarkup = Markup.InlineKeyboardMarkup keyboard
                        return Some (baseReply |> Reply.withMarkup replyMarkup)
                    else
                        return Some baseReply
            with ex ->
                Log.Error(ex, "Error processing Twitter link")
                twitterFailureCounter.Inc()
                return None
        }

    let getTwitterLinks (message: string option) = getLinks twitterRegex message

type TwitterLinksHandler() =
    inherit BaseHandler()

    member private this.extractTwitterLinks =
        createLinkExtractor Twitter.getTwitterLinks TwitterMessage

    member this.Handle(msg: UpdateMessage) : Task =
        let links = this.extractTwitterLinks msg
        task {
            for message in links do
                do! publishToBusAsync message |> Async.StartAsTask
        }

    member this.Handle(msg: TwitterMessage) =
        this.processLinkAsync msg (Twitter.getTwitterReplyAsync msg.OriginalMessage.ChatId)
