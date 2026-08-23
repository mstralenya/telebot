module Telebot.Tests.InfraTests

open System
open Xunit
open Telebot.Config
open Telebot.HttpClient

/// Starts a local server answering 503 once and 200 afterwards; returns (url, hitCount ref, stop)
let private startFlakyServer () =
    let hits = ref 0
    let mutable port = 25080
    let mutable listener: System.Net.HttpListener = null

    while isNull listener do
        try
            listener <- new System.Net.HttpListener()
            listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
            listener.Start()
        with _ ->
            listener <- null
            port <- port + 1
            if port > 25200 then failwith "no free port for test server"

    do
        async {
            while listener.IsListening do
                try
                    let! ctx = listener.GetContextAsync() |> Async.AwaitTask
                    incr hits
                    let body = Text.Encoding.UTF8.GetBytes "payload"
                    ctx.Response.StatusCode <- if !hits = 1 then 503 else 200
                    ctx.Response.ContentLength64 <- int64 body.Length
                    do! ctx.Response.OutputStream.WriteAsync(body, 0, body.Length) |> Async.AwaitTask
                    ctx.Response.Close()
                with _ -> ()
        }
        |> Async.Start

    sprintf "http://127.0.0.1:%d/retry-test" port,
    hits,
    (fun () -> listener.Stop())

[<Fact>]
let ``getAsync retries transient 503 responses with a fresh request`` () =
    // Regression test: re-sending the same HttpRequestMessage throws
    // InvalidOperationException ("request message was already sent")
    let url, hits, stop = startFlakyServer ()
    try
        initializeHttpClientFactory () |> ignore
        use response = getAsync url false |> Async.RunSynchronously
        Assert.Equal(Net.HttpStatusCode.OK, response.StatusCode)
        Assert.Equal(2, !hits)
    finally
        stop ()

[<Theory>]
[<InlineData("", "https://localhost/")>]
[<InlineData("https://example.com/path?q=1", "https://example.com/path?q=1")>]
[<InlineData("//cdn.example.com/video.mp4", "https://cdn.example.com/video.mp4")>]
[<InlineData("/media/photo.jpg", "https://www.instagram.com/media/photo.jpg")>]
let ``normalizeUri handles relative and scheme-relative urls`` (input, expected) =
    Assert.Equal(expected, (normalizeUri input).ToString())

[<Fact>]
let ``normalizeUri adds scheme to bare hosts`` () =
    let uri = normalizeUri "example.com/media.mp4"
    Assert.Equal("https://example.com/media.mp4", uri.ToString())

[<Fact>]
let ``parseIdSet parses comma separated ids`` () =
    Environment.SetEnvironmentVariable("TEST_IDS", "1, 2,3 ;bad;42")
    try
        let ids = parseIdSet "TEST_IDS"
        Assert.Equal<int64 Set>(Set.ofList [ 1L; 2L; 3L; 42L ], ids)
    finally
        Environment.SetEnvironmentVariable("TEST_IDS", null)

[<Fact>]
let ``parseIdSet returns empty set for missing or empty values`` () =
    Environment.SetEnvironmentVariable("TEST_IDS_MISSING", "")
    let ids = parseIdSet "TEST_IDS_MISSING"
    Assert.Empty(ids)
