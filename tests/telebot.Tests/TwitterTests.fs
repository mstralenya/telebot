module Telebot.Tests.TwitterTests

open System.Text.Json
open Xunit
open Telebot.TwitterData
open Telebot.Twitter.Twitter
open Telebot.Replies

let private qrt =
    {
        allSameType = true
        combinedMediaUrl = None
        communityNote = None
        conversationID = "1"
        date = "2026-01-01"
        date_epoch = 0L
        hasMedia = false
        mediaURLs = []
        media_extended = []
        qrtURL = "https://x.com/q/status/2"
        text = Some "quoted text"
        tweetID = "2"
        tweetURL = "https://x.com/q/status/2"
        user_name = "Quoted User"
        user_profile_image_url = ""
        user_screen_name = "quoteduser"
        translation = None
    }

[<Fact>]
let ``renderTweet formats simple tweet`` () =
    let result = renderTweet "alice" "Alice" (Some "hello world") None None
    Assert.StartsWith("<b>Alice</b>", result)
    Assert.Contains("<blockquote>hello world</blockquote>", result)

[<Fact>]
let ``renderTweet formats tweet with quote`` () =
    let result = renderTweet "alice" "Alice" (Some "main") (Some qrt) (Some "qtext")
    Assert.Contains("Quoting <b>Quoted User</b>", result)
    Assert.Contains("<blockquote>main</blockquote>", result)
    Assert.Contains("<blockquote>qtext</blockquote>", result)

[<Fact>]
let ``renderTweet without quote body falls back to plain blockquote`` () =
    let result = renderTweet "alice" "Alice" (Some "main") (Some qrt) None
    Assert.DoesNotContain("Quoting", result)
    Assert.Contains("<blockquote>main</blockquote>", result)

[<Fact>]
let ``renderTweet with no body shows author only`` () =
    // The source uses "@<zero-width-space><name>"; reproduce exactly
    let zwsp = string (char 0x200B)
    let result = renderTweet "alice" "Alice" None None None
    Assert.Equal($"<b>Alice</b> <i>(@{zwsp}alice)</i>:", result)

[<Fact>]
let ``renderTweet original matches translated shape for same content`` () =
    // The "original" rendering path uses the same formatter; verify both calls agree
    let a = renderTweet "bob" "Bob" (Some "t") (Some qrt) (qrt.text)
    let b = renderTweet "bob" "Bob" (Some "t") (Some qrt) (Some "quoted text")
    Assert.Equal(b, a)

[<Fact>]
let ``FxTwitter quoted tweet accepts object-valued community note`` () =
    let json = """{
      "code": 200,
      "message": "OK",
      "tweet": {
        "id": "main",
        "url": "https://x.com/user/status/main",
        "text": "main text",
        "author": { "name": "Main", "screen_name": "main", "avatar_url": null },
        "media": null,
        "translation": null,
        "community_note": null,
        "created_at": null,
        "created_timestamp": 1,
        "quote": {
          "id": "quoted",
          "url": "https://x.com/user/status/quoted",
          "text": "quoted text",
          "author": { "name": "Quoted", "screen_name": "quoted", "avatar_url": null },
          "media": null,
          "translation": null,
          "community_note": { "text": "Context for this post", "entities": [] },
          "created_at": null,
          "created_timestamp": 2,
          "quote": null
        }
      }
    }"""

    let response = JsonSerializer.Deserialize<FxTweetResponse>(json)
    let tweet = FxConverter.toTweet response.tweet

    Assert.Equal(Some "Context for this post", tweet.qrt |> Option.bind _.communityNote)

[<Fact>]
let ``Twitter extractor accepts links without a scheme`` () =
    let url = "x.com/venturetwins/status/2106788740563873837"
    Assert.Equal<string list>([ url ], getLinks twitterRegex (Some url))
    Assert.Equal("https://x.com/venturetwins/status/2106788740563873837", normalizeTwitterUrl url)

[<Fact>]
let ``Twitter translation language is inserted before query parameters`` () =
    let url = "https://x.com/jimbo4xl/status/2107870845532258634?s=46&t=tracking"
    let result = buildTwitterApiUrl "https://api.fxtwitter.com/" (Some "ru") url
    Assert.Equal("https://api.fxtwitter.com/jimbo4xl/status/2107870845532258634/ru?s=46&t=tracking", result)

[<Fact>]
let ``Twitter API URL is unchanged by translation handling when language is disabled`` () =
    let url = "https://twitter.com/user/status/123?ref=test"
    let result = buildTwitterApiUrl "https://api.fxtwitter.com" None url
    Assert.Equal("https://api.fxtwitter.com/user/status/123?ref=test", result)
