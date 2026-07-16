module Telebot.HttpClient

open System
open System.Diagnostics
open System.Net.Http
open Microsoft.Extensions.DependencyInjection
open Polly
open Polly.Extensions.Http
open Telebot.PrometheusMetrics
open Telebot.TelemetryService

module ProxyConfig =
    let getProxyUrl () =
        let url = Environment.GetEnvironmentVariable("PROXY_URL")
        if String.IsNullOrWhiteSpace(url) then None else Some url

    let useProxyForInstagramReels () =
        let env = Environment.GetEnvironmentVariable("USE_PROXY_FOR_INSTAGRAM_REELS")
        not (String.IsNullOrWhiteSpace(env)) && (env.Equals("true", StringComparison.OrdinalIgnoreCase) || env = "1")

    let useProxyForInstagramPosts () =
        let env = Environment.GetEnvironmentVariable("USE_PROXY_FOR_INSTAGRAM_POSTS")
        not (String.IsNullOrWhiteSpace(env)) && (env.Equals("true", StringComparison.OrdinalIgnoreCase) || env = "1")

    let useProxyForTikTok () =
        let env = Environment.GetEnvironmentVariable("USE_PROXY_FOR_TIKTOK")
        not (String.IsNullOrWhiteSpace(env)) && (env.Equals("true", StringComparison.OrdinalIgnoreCase) || env = "1")

    let useProxyForTwitter () =
        let env = Environment.GetEnvironmentVariable("USE_PROXY_FOR_TWITTER")
        not (String.IsNullOrWhiteSpace(env)) && (env.Equals("true", StringComparison.OrdinalIgnoreCase) || env = "1")

    let useProxyForYoutube () =
        let env = Environment.GetEnvironmentVariable("USE_PROXY_FOR_YOUTUBE")
        not (String.IsNullOrWhiteSpace(env)) && (env.Equals("true", StringComparison.OrdinalIgnoreCase) || env = "1")

module private Constants =
    [<Literal>]
    let UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3"

    [<Literal>]
    let DefaultTimeoutSeconds = 30

// Retry policy for HTTP requests
let private retryPolicy =
    HttpPolicyExtensions
        .HandleTransientHttpError()
        .Or<Polly.Timeout.TimeoutRejectedException>()
        .WaitAndRetryAsync(
            retryCount = 3,
            sleepDurationProvider = fun retryAttempt -> TimeSpan.FromSeconds(Math.Pow(2.0, float retryAttempt))
        )

// Timeout policy
let private timeoutPolicy =
    Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(120.0))

// Combined policy
let private combinedPolicy =
    Policy.WrapAsync(retryPolicy, timeoutPolicy)

// Service provider for dependency injection
let mutable private serviceProvider: ServiceProvider option = None

// Initialize HTTP client factory
let initializeHttpClientFactory () =
    let services = ServiceCollection()

    services
        .AddHttpClient("telebot")
        .AddPolicyHandler(combinedPolicy)
        .ConfigureHttpClient(Action<HttpClient>(fun client -> client.Timeout <- TimeSpan.FromSeconds(120.0)))
        .ConfigurePrimaryHttpMessageHandler(
            Func<HttpMessageHandler>(fun () -> 
                let handler = new HttpClientHandler(CookieContainer = System.Net.CookieContainer())
                handler :> HttpMessageHandler  // Explicit upcast to HttpMessageHandler
            )
        ) |> ignore

    services
        .AddHttpClient("telebot_proxied")
        .AddPolicyHandler(combinedPolicy)
        .ConfigureHttpClient(Action<HttpClient>(fun client -> client.Timeout <- TimeSpan.FromSeconds(120.0)))
        .ConfigurePrimaryHttpMessageHandler(
            Func<HttpMessageHandler>(fun () -> 
                let handler = new HttpClientHandler(CookieContainer = System.Net.CookieContainer())
                let proxyUrl = ProxyConfig.getProxyUrl()
                match proxyUrl with
                | Some url ->
                    handler.Proxy <- System.Net.WebProxy(url)
                    handler.UseProxy <- true
                | None -> ()
                handler :> HttpMessageHandler  // Explicit upcast to HttpMessageHandler
            )
        ) |> ignore

    services
        .AddHttpClient("telebot_health")
        .ConfigureHttpClient(Action<HttpClient>(fun client -> client.Timeout <- TimeSpan.FromSeconds(10.0)))
        .ConfigurePrimaryHttpMessageHandler(
            Func<HttpMessageHandler>(fun () -> 
                let handler = new HttpClientHandler(CookieContainer = System.Net.CookieContainer())
                handler :> HttpMessageHandler  // Explicit upcast to HttpMessageHandler
            )
        ) |> ignore

    let provider = services.BuildServiceProvider()
    serviceProvider <- Some provider
    provider

// Get HTTP client from factory
let private getHttpClient (useProxy: bool) =
    let clientName = if useProxy then "telebot_proxied" else "telebot"
    match serviceProvider with
    | Some provider ->
        let factory = provider.GetRequiredService<IHttpClientFactory>()
        factory.CreateClient(clientName)
    | None ->
        let provider = initializeHttpClientFactory()
        let factory = provider.GetRequiredService<IHttpClientFactory>()
        factory.CreateClient(clientName)

// Record HTTP metrics
let private recordHttpMetrics (method: string) (uri: Uri) (responseMessage: HttpResponseMessage) (duration: TimeSpan) (requestSize: int64 option) (responseSize: int64 option) =
    let host = uri.Host
    let statusCode = responseMessage.StatusCode.ToString()

    httpRequestsTotal.WithLabels([|method; host; statusCode|]).Inc()
    httpRequestDuration.WithLabels([|method; host|]).Observe(duration.TotalSeconds)

    requestSize |> Option.iter (fun size -> httpRequestSize.WithLabels([|method; host|]).Observe(float size))
    responseSize |> Option.iter (fun size -> httpResponseSize.WithLabels([|method; host|]).Observe(float size))

// Async HTTP request with telemetry
let private executeHttpRequestAsync (useProxy: bool) (request: HttpRequestMessage) : Async<HttpResponseMessage> =
    async {
        use client = getHttpClient useProxy
        if not (request.Headers.Contains("User-Agent")) then
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:138.0) Gecko/20100101 Firefox/138.0")
        if not (request.Headers.Contains("Accept")) then
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8")
        if not (request.Headers.Contains("Accept-Language")) then
            request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.5")
        if not (request.Headers.Contains("Sec-Fetch-Dest")) then
            request.Headers.Add("Sec-Fetch-Dest", "document")
        if not (request.Headers.Contains("Sec-Fetch-Mode")) then
            request.Headers.Add("Sec-Fetch-Mode", "navigate")
        if not (request.Headers.Contains("Sec-Fetch-Site")) then
            request.Headers.Add("Sec-Fetch-Site", "none")
        if not (request.Headers.Contains("Sec-Fetch-User")) then
            request.Headers.Add("Sec-Fetch-User", "?1")
        if not (request.Headers.Contains("Upgrade-Insecure-Requests")) then
            request.Headers.Add("Upgrade-Insecure-Requests", "1")
        if not (request.Headers.Contains("DNT")) then
            request.Headers.Add("DNT", "1")
        let stopwatch = Stopwatch.StartNew()

        try
            activeConnectionsGauge.Inc()

            let! response = client.SendAsync(request) |> Async.AwaitTask
            stopwatch.Stop()

            let requestSize =
                match request.Content with
                | null -> None
                | content ->
                    match content.Headers.ContentLength with
                    | length when length.HasValue -> Some length.Value
                    | _ -> None
            
            let responseSize =
                match response.Content.Headers.ContentLength with
                | length when length.HasValue -> Some length.Value
                | _ -> None

            recordHttpMetrics request.Method.Method request.RequestUri response stopwatch.Elapsed requestSize responseSize

            return response
        finally
            activeConnectionsGauge.Dec()
            stopwatch.Stop()
    }

// GET request with telemetry
let getAsync (url: string) (useProxy: bool) : Async<HttpResponseMessage> =
    withOperationTelemetry "http_get" (fun scope ->
        async {
            TelemetryScope.addProperty "url" url scope |> ignore
            TelemetryScope.addProperty "use_proxy" useProxy scope |> ignore
            TelemetryScope.logInfo $"Making GET request to {url} (useProxy={useProxy})" scope

            use request = new HttpRequestMessage(HttpMethod.Get, url)
            let! response = executeHttpRequestAsync useProxy request

            if response.IsSuccessStatusCode then
                TelemetryScope.logInfo $"GET request successful: {response.StatusCode}" scope
            else
                TelemetryScope.logWarning $"GET request failed: {response.StatusCode}" scope

            return response
        }
    )

// POST request with telemetry
let postAsync (url: string) (content: HttpContent) (useProxy: bool) : Async<HttpResponseMessage> =
    withOperationTelemetry "http_post" (fun scope ->
        async {
            TelemetryScope.addProperty "url" url scope |> ignore
            TelemetryScope.addProperty "use_proxy" useProxy scope |> ignore
            TelemetryScope.logInfo $"Making POST request to {url} (useProxy={useProxy})" scope

            use request = new HttpRequestMessage(HttpMethod.Post, url)
            request.Content <- content
            let! response = executeHttpRequestAsync useProxy request

            if response.IsSuccessStatusCode then
                TelemetryScope.logInfo $"POST request successful: {response.StatusCode}" scope
            else
                TelemetryScope.logWarning $"POST request failed: {response.StatusCode}" scope

            return response
        }
    )

// Execute custom request with telemetry
let executeRequestAsync (request: HttpRequestMessage) (useProxy: bool) : Async<HttpResponseMessage> =
    withOperationTelemetry "http_custom" (fun scope ->
        async {
            let url = request.RequestUri.ToString()
            let method = request.Method.Method
            TelemetryScope.addProperty "url" url scope |> ignore
            TelemetryScope.addProperty "method" method scope |> ignore
            TelemetryScope.addProperty "use_proxy" useProxy scope |> ignore
            TelemetryScope.logInfo $"Making {method} request to {url} (useProxy={useProxy})" scope

            let! response = executeHttpRequestAsync useProxy request

            if response.IsSuccessStatusCode then
                TelemetryScope.logInfo $"{method} request successful: {response.StatusCode}" scope
            else
                TelemetryScope.logWarning $"{method} request failed: {response.StatusCode}" scope

            return response
        }
    )

// Download data as byte array
let downloadBytesAsync (url: string) (useProxy: bool) : Async<byte[]> =
    async {
        let! response = getAsync url useProxy
        response.EnsureSuccessStatusCode() |> ignore
        return! response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
    }

// Download data as string
let downloadStringAsync (url: string) (useProxy: bool) : Async<string> =
    async {
        let! response = getAsync url useProxy
        response.EnsureSuccessStatusCode() |> ignore
        return! response.Content.ReadAsStringAsync() |> Async.AwaitTask
    }

// Health check function
let healthCheckAsync () : Async<bool> =
    async {
        try
            let clientName = "telebot_health"
            use client = 
                match serviceProvider with
                | Some provider ->
                    let factory = provider.GetRequiredService<IHttpClientFactory>()
                    factory.CreateClient(clientName)
                | None ->
                    let provider = initializeHttpClientFactory()
                    let factory = provider.GetRequiredService<IHttpClientFactory>()
                    factory.CreateClient(clientName)
            let! response = client.GetAsync("https://api.telegram.org") |> Async.AwaitTask
            let isHealthy = response.IsSuccessStatusCode || response.StatusCode = System.Net.HttpStatusCode.NotFound
            healthCheckStatus.WithLabels([|"http_client"|]).Set(if isHealthy then 1.0 else 0.0)
            return isHealthy
        with
        | _ ->
            healthCheckStatus.WithLabels([|"http_client"|]).Set(0.0)
            return false
    }

// Cleanup resources
let cleanup () =
    match serviceProvider with
    | Some provider ->
        provider.Dispose()
        serviceProvider <- None
    | None -> ()
