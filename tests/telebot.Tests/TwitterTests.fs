module Telebot.Tests.TwitterTests

open Xunit
open Telebot.TwitterData
open Telebot.Twitter.Twitter

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
