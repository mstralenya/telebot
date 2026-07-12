module Telebot.Twitter

open System.Linq
open System.Net.Http.Json
open System.Text.Json
open System.Text.RegularExpressions
open Serilog
open Telebot.Bus
open Telebot.Handlers
open Telebot.PrometheusMetrics
open Telebot.Messages
open Telebot.Text
open Telebot.TwitterData
open Telebot.VideoDownloader

module Twitter =
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

    let twitterRegex =
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
            try
                let! tweetOpt = getTweetFromUrlAsync url
                match tweetOpt with
                | None ->
                    twitterFailureCounter.Inc()
                    return None
                | Some tweet ->
                    let! tweet =
                        async {
                            let envLang = System.Environment.GetEnvironmentVariable("TWITTER_TRANSLATION_LANG")
                            let isLlmEnabled = Translation.getLlmApiUrl().IsSome
                            if isLlmEnabled && not (System.String.IsNullOrWhiteSpace(envLang)) then
                                let targetLang = envLang.Trim()
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
                            else
                                return tweet
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

                    let mainText =
                        match tweet.user_screen_name, tweet.user_name, textToUse with
                        | ah, a, Some t -> $"<b>{a}</b> <i>(@​{ah})</i>: <blockquote>{t}</blockquote>"
                        | ah, a, _ -> $"<b>{a}</b> <i>(@​{ah})</i>:"

                    let mainMedia = formatMedia tweet.media_extended

                    let quotedContent =
                        match tweet.qrt with
                        | Some qrt ->
                            let qrtText =
                                match qrt.user_screen_name, qrt.user_name, qrtTextToUse with
                                | qah, qa, Some qtxt -> $"Quoting <b>{qa}</b> <i>(@​{qah})</i>: <blockquote>{qtxt}</blockquote>"
                                | qah, qa, _ -> $"Quoting <b>{qa}</b> <i>(@​{qah})</i>:"
                            let qrtMedia = formatMedia qrt.media_extended
                            qrtText + qrtMedia
                        | None -> ""

                    let html = mainText + mainMedia + quotedContent
                    twitterSuccessCounter.Inc()
                    return Some (Reply.createRichMessage html)
            with ex ->
                Log.Error(ex, "Error processing Twitter link")
                twitterFailureCounter.Inc()
                return None
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
