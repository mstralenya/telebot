module Telebot.Twitter

open Funogram.Telegram.Types
open System
open System.Net
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
    let internal normalizeTwitterUrl (url: string) =
        if url.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase) then
            url
        else
            $"https://{url}"

    let internal buildTwitterApiUrl (apiBase: string) (translationLang: string option) (url: string) =
        let url = normalizeTwitterUrl url
        let apiBase = apiBase.TrimEnd('/') + "/"
        let replaced =
            url.Replace("https://x.com/", apiBase)
               .Replace("https://twitter.com/", apiBase)

        match translationLang with
        | None -> replaced
        | Some langCode ->
            let builder = UriBuilder(replaced)
            builder.Path <- $"{builder.Path.TrimEnd('/')}/{langCode}"
            builder.Uri.AbsoluteUri

    // Function to replace the domain in the URL and append translation language suffix if configured
    let private replaceDomain (url: string) =
        let config = Config.get ()
        buildTwitterApiUrl config.TwitterApiBase config.TwitterTranslationLang url

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
        Regex(
            @"(?:https?://)?(?:www\.)?(?:x|twitter)\.com/[A-Za-z0-9_]+/status/[0-9]+(?:\?[^\s]*)?",
            RegexOptions.Compiled ||| RegexOptions.IgnoreCase
        )

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
        let main = $"<b>{WebUtility.HtmlEncode userName}</b> <i>(@​{WebUtility.HtmlEncode screenName})</i>:"
        let main = main + (body |> Option.map (fun text -> $" <blockquote>{text}</blockquote>") |> Option.defaultValue "")
        match qrt with
        | Some q ->
            let quoted = qrtBody |> Option.map (fun text -> $"<blockquote>{text}</blockquote>") |> Option.defaultValue ""
            main + $"Quoting <b>{WebUtility.HtmlEncode q.user_name}</b><i>(@​{WebUtility.HtmlEncode q.user_screen_name})</i>:" + quoted
        | None -> main

    let internal renderMedia (media: TwitterMediaExtended list) =
        let items =
            media
            |> List.choose (fun item ->
                match Uri.TryCreate(item.url, UriKind.Absolute) with
                | true, uri when uri.Scheme = "https" || uri.Scheme = "http" ->
                    let tag = if item.mediaType = TwitterMedia.Video then "video" else "img"
                    Some $"<{tag} src=\"{WebUtility.HtmlEncode item.url}\"/>"
                | _ -> None)
            |> String.concat ""
        if items = "" then "" else $"<tg-collage>{items}</tg-collage>"

    let internal renderRichTweet (tweet: Tweet) body quotedBody =
        let main = renderTweet tweet.user_screen_name tweet.user_name body None None + renderMedia tweet.media_extended
        match tweet.qrt with
        | Some quote ->
            main + "Quoting " + renderTweet quote.user_screen_name quote.user_name quotedBody None None + renderMedia quote.media_extended
        | None -> main

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
                                let mainTranslation =
                                    match tweet.text with
                                    | Some txt when not (System.String.IsNullOrWhiteSpace(txt)) -> Translation.translateTextAsync txt targetLang
                                    | _ -> async { return None }

                                let quotedTranslation =
                                    match tweet.qrt |> Option.bind _.text with
                                    | Some txt when not (System.String.IsNullOrWhiteSpace(txt)) -> Translation.translateTextAsync txt targetLang
                                    | _ -> async { return None }

                                let! translations = Async.Parallel [ mainTranslation; quotedTranslation ]
                                let mainTl, qrtTl = translations[0], translations[1]
                                
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
                            Some $"<i>【TL {WebUtility.HtmlEncode direction}】</i>\n{WebUtility.HtmlEncode tl.text}"
                        | _ -> tweet.text |> Option.map WebUtility.HtmlEncode

                    let qrtTextToUse =
                        match tweet.qrt with
                        | Some qrt ->
                            match qrt.translation with
                            | Some tl when not (System.String.IsNullOrWhiteSpace(tl.text)) ->
                                let direction = $"{tl.source_language.ToUpper()}→{tl.destination_language.ToUpper()}"
                                Log.Information("Successfully retrieved translation for quoted tweet {TweetId} ({Direction})", qrt.tweetID, direction)
                                Some $"<i>【TL {WebUtility.HtmlEncode direction}】</i>\n{WebUtility.HtmlEncode tl.text}"
                            | _ -> qrt.text |> Option.map WebUtility.HtmlEncode
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

                    let richText = renderRichTweet tweet textToUse qrtTextToUse

                    // Check if translation was actually applied to add a "Show Original Text" button
                    let hasTranslation =
                        (tweet.translation |> Option.bind (fun tl -> if System.String.IsNullOrWhiteSpace(tl.text) then None else Some tl) |> Option.isSome) ||
                        (tweet.qrt |> Option.bind (fun q -> q.translation |> Option.bind (fun tl -> if System.String.IsNullOrWhiteSpace(tl.text) then None else Some tl)) |> Option.isSome)

                    let addTranslationMarkup reply original translated =
                        if not hasTranslation then reply
                        else
                        let cacheId = Translation.saveTranslationToCache original translated
                        let btnToggle = InlineKeyboardButton.Create("Show Original Text", callbackData = $"show_orig:{cacheId}")
                        let isGroupChat =
                            match chatId with
                            | ChatId.Int id -> id < 0L
                            | ChatId.String s -> s.StartsWith("-")
                        let buttons =
                            match Config.get().WebAppBaseUrl with
                            | Some webAppBase when not isGroupChat ->
                                let url = $"{webAppBase.TrimEnd('/')}/webapp?id={cacheId}"
                                let btnWebApp = InlineKeyboardButton.Create("Original (Web)", webApp = WebAppInfo.Create(url))
                                [| [| btnWebApp; btnToggle |] |]
                            | _ -> [| [| btnToggle |] |]
                        let keyboard = InlineKeyboardMarkup.Create(buttons)
                        reply |> Reply.withMarkup (Markup.InlineKeyboardMarkup keyboard)

                    let originalBody = tweet.text |> Option.map WebUtility.HtmlEncode
                    let originalQuoteBody = tweet.qrt |> Option.bind _.text |> Option.map WebUtility.HtmlEncode
                    let originalText = renderTweet tweet.user_screen_name tweet.user_name originalBody tweet.qrt originalQuoteBody
                    let fallback = addTranslationMarkup baseReply originalText replyText.Value
                    let richReply = Telebot.DataTypes.Reply.RichMessage(richText, None, fallback)
                    return Some(addTranslationMarkup richReply (renderRichTweet tweet originalBody originalQuoteBody) richText)

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
