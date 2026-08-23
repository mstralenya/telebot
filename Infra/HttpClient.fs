module Telebot.HttpClient

open System
open System.Diagnostics
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Serilog
open Telebot.PrometheusMetrics
open Telebot.TelemetryService

module ProxyConfig =
    let getProxyUrl () = Config.get().ProxyUrl

    let useProxyForInstagramReels () = Config.get().UseProxyForInstagramReels
    let useProxyForInstagramPosts () = Config.get().UseProxyForInstagramPosts
    let useProxyForTikTok () = Config.get().UseProxyForTikTok
    let useProxyForTwitter () = Config.get().UseProxyForTwitter
    let useProxyForYoutube () = Config.get().UseProxyForYoutube

// Retry configuration for transient HTTP failures.
// NOTE: retries are implemented here (not as an HttpClient policy handler) because
// HttpRequestMessage instances are single-use: re-sending the same instance through
// a delegating handler throws "The request message was already sent".
let private maxRetryAttempts = 3

let private isTransientStatusCode (status: HttpStatusCode) =
    int status >= 500 || status = HttpStatusCode.RequestTimeout

let private isTransientException (ex: exn) =
    match ex with
    | :? HttpRequestException -> true
    // HttpClient.Timeout expiry surfaces as TaskCanceledException/TimeoutException
    | :? TaskCanceledException -> true
    | :? TimeoutException -> true
    | :? IO.IOException -> true
    | _ -> false

// Exponential backoff: 2, 4, 8 seconds like the previous Polly policy
let private retryDelayMs attemptNumber = int (1000.0 * Math.Pow(2.0, float attemptNumber))

/// Clones a request so it can be resent after a transient failure.
let private cloneRequestAsync (request: HttpRequestMessage) : Async<HttpRequestMessage> =
    async {
        let clone = new HttpRequestMessage(request.Method, request.RequestUri)
        clone.Version <- request.Version
        clone.VersionPolicy <- request.VersionPolicy

        for header in request.Headers do
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value) |> ignore

        match Option.ofObj request.Content with
        | None -> ()
        | Some content ->
            // Small bodies only (form posts); buffered so they can be replayed
            let! bytes = content.ReadAsByteArrayAsync() |> Async.AwaitTask
            let clonedContent = new ByteArrayContent(bytes)
            for header in content.Headers do
                clonedContent.Headers.TryAddWithoutValidation(header.Key, header.Value) |> ignore
            clone.Content <- clonedContent

        return clone
    }

// Service provider for dependency injection
let mutable private serviceProvider: ServiceProvider option = None

let private addNamedClient
    (services: IServiceCollection)
    (name: string)
    (timeoutSeconds: int)
    (createHandler: unit -> HttpMessageHandler)
    =
    services.AddHttpClient(name)
        .ConfigureHttpClient(Action<HttpClient>(fun client -> client.Timeout <- TimeSpan.FromSeconds(float timeoutSeconds)))
        .ConfigurePrimaryHttpMessageHandler(Func<HttpMessageHandler>(createHandler))
    |> ignore

// Initialize HTTP client factory
let initializeHttpClientFactory () =
    let services = ServiceCollection()

    let defaultHandler () =
        new HttpClientHandler(CookieContainer = CookieContainer()) :> HttpMessageHandler

    let proxiedHandler () =
        let handler = new HttpClientHandler(CookieContainer = CookieContainer())
        ProxyConfig.getProxyUrl()
        |> Option.iter (fun url ->
            handler.Proxy <- WebProxy(url)
            handler.UseProxy <- true)
        handler :> HttpMessageHandler

    addNamedClient services "telebot" 120 defaultHandler
    addNamedClient services "telebot_proxied" 120 proxiedHandler
    addNamedClient services "telebot_health" 10 defaultHandler
    addNamedClient services "telebot_llm" 120 defaultHandler

    let provider = services.BuildServiceProvider()
    serviceProvider <- Some provider
    provider

let private getClient (clientName: string) =
    let provider =
        match serviceProvider with
        | Some provider -> provider
        | None -> initializeHttpClientFactory()
    let factory = provider.GetRequiredService<IHttpClientFactory>()
    factory.CreateClient(clientName)

// Get HTTP client from factory
let private getHttpClient (useProxy: bool) =
    getClient (if useProxy then "telebot_proxied" else "telebot")

/// Creates a client for LLM API calls with a generous timeout and no automatic retries.
let createLlmClient () : HttpClient = getClient "telebot_llm"

// Record HTTP metrics
let private recordHttpMetrics (method: string) (uri: Uri) (responseMessage: HttpResponseMessage) (duration: TimeSpan) (requestSize: int64 option) (responseSize: int64 option) =
    let host = uri.Host
    let statusCode = responseMessage.StatusCode.ToString()

    httpRequestsTotal.WithLabels([|method; host; statusCode|]).Inc()
    httpRequestDuration.WithLabels([|method; host|]).Observe(duration.TotalSeconds)

    requestSize |> Option.iter (fun size -> httpRequestSize.WithLabels([|method; host|]).Observe(float size))
    responseSize |> Option.iter (fun size -> httpResponseSize.WithLabels([|method; host|]).Observe(float size))

// Browser-like default headers applied to requests that don't set their own
let private defaultHeaders =
    [ "User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:138.0) Gecko/20100101 Firefox/138.0"
      "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
      "Accept-Language", "en-US,en;q=0.5"
      "Sec-Fetch-Dest", "document"
      "Sec-Fetch-Mode", "navigate"
      "Sec-Fetch-Site", "none"
      "Sec-Fetch-User", "?1"
      "Upgrade-Insecure-Requests", "1"
      "DNT", "1" ]

let private applyDefaultHeaders (request: HttpRequestMessage) =
    defaultHeaders
    |> List.iter (fun (name, value) ->
        if not (request.Headers.Contains name) then
            request.Headers.TryAddWithoutValidation(name, value) |> ignore)

let private contentLength (content: HttpContent) =
    let length = content.Headers.ContentLength
    if length.HasValue then Some length.Value else None

// Async HTTP request with telemetry and transient-failure retries.
// Each retry attempt uses a fresh clone of the request.
let private executeHttpRequestAsync (useProxy: bool) (request: HttpRequestMessage) : Async<HttpResponseMessage> =
    async {
        // Factory-created clients wrap pooled handlers and must not be disposed
        let client = getHttpClient useProxy
        applyDefaultHeaders request

        let rec send attemptsLeft current =
            async {
                let stopwatch = Stopwatch.StartNew()
                try
                    activeConnectionsGauge.Inc()
                    try
                        let! response = client.SendAsync(current) |> Async.AwaitTask
                        stopwatch.Stop()

                        if attemptsLeft > 0 && isTransientStatusCode response.StatusCode then
                            let status = int response.StatusCode
                            response.Dispose()
                            Log.Warning("Transient HTTP {Status} from {Host}, retrying...", status, current.RequestUri.Host)
                            do! Async.Sleep(retryDelayMs (maxRetryAttempts - attemptsLeft + 1))
                            let! freshRequest = cloneRequestAsync current
                            return! send (attemptsLeft - 1) freshRequest
                        else
                            let requestSize =
                                match Option.ofObj current.Content with
                                | Some content -> contentLength content
                                | None -> None
                            recordHttpMetrics current.Method.Method current.RequestUri response stopwatch.Elapsed
                                requestSize
                                (contentLength response.Content)
                            return response
                    finally
                        activeConnectionsGauge.Dec()
                with
                | ex when attemptsLeft > 0 && isTransientException ex ->
                    Log.Warning(ex, "Transient HTTP failure calling {Host}, retrying...", current.RequestUri.Host)
                    do! Async.Sleep(retryDelayMs (maxRetryAttempts - attemptsLeft + 1))
                    let! freshRequest = cloneRequestAsync current
                    return! send (attemptsLeft - 1) freshRequest
            }

        return! send maxRetryAttempts request
    }

let internal normalizeUri (url: string) : Uri =
    if String.IsNullOrWhiteSpace(url) then
        Uri("https://localhost")
    elif Uri.IsWellFormedUriString(url, UriKind.Absolute) then
        Uri(url)
    elif url.StartsWith("//") then
        Uri($"https:{url}")
    elif url.StartsWith("/") then
        Uri($"https://www.instagram.com{url}")
    else
        try Uri(url, UriKind.Absolute) with _ -> Uri($"https://{url}")

// Shared request execution with operation telemetry and result logging
let private sendTrackedAsync
    (operationName: string)
    (methodLabel: string)
    (url: string)
    (useProxy: bool)
    (request: HttpRequestMessage) : Async<HttpResponseMessage> =
    withOperationTelemetry operationName (fun scope ->
        async {
            TelemetryScope.addProperty "url" url scope |> ignore
            TelemetryScope.addProperty "method" methodLabel scope |> ignore
            TelemetryScope.addProperty "use_proxy" useProxy scope |> ignore
            TelemetryScope.logInfo $"Making {methodLabel} request to {url} (useProxy={useProxy})" scope

            let! response = executeHttpRequestAsync useProxy request

            if response.IsSuccessStatusCode then
                TelemetryScope.logInfo $"{methodLabel} request successful: {response.StatusCode}" scope
            else
                TelemetryScope.logWarning $"{methodLabel} request failed: {response.StatusCode}" scope

            return response
        }
    )

// GET request with telemetry.
// NOTE: the request must be created inside the async workflow — creating it in plain
// function scope would dispose it before the workflow runs ("request already sent").
let getAsync (url: string) (useProxy: bool) : Async<HttpResponseMessage> =
    async {
        use request = new HttpRequestMessage(HttpMethod.Get, normalizeUri url)
        return! sendTrackedAsync "http_get" "GET" url useProxy request
    }

// POST request with telemetry
let postAsync (url: string) (content: HttpContent) (useProxy: bool) : Async<HttpResponseMessage> =
    async {
        use request = new HttpRequestMessage(HttpMethod.Post, normalizeUri url, Content = content)
        return! sendTrackedAsync "http_post" "POST" url useProxy request
    }

// Execute custom request with telemetry
let executeRequestAsync (request: HttpRequestMessage) (useProxy: bool) : Async<HttpResponseMessage> =
    sendTrackedAsync "http_custom" request.Method.Method (request.RequestUri.ToString()) useProxy request

// Health check function
let healthCheckAsync () : Async<bool> =
    async {
        try
            // Factory-created clients wrap pooled handlers and must not be disposed
            let client = getClient "telebot_health"
            let! response = client.GetAsync("https://api.telegram.org") |> Async.AwaitTask
            let isHealthy = response.IsSuccessStatusCode || response.StatusCode = HttpStatusCode.NotFound
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
