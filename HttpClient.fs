module Telebot.HttpClient

open System
open System.Diagnostics
open System.Net.Http
open Microsoft.Extensions.DependencyInjection
open Polly
open Polly.Extensions.Http
open Telebot.PrometheusMetrics
open Telebot.TelemetryService

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
        .WaitAndRetryAsync(
            retryCount = 3,
            sleepDurationProvider = fun retryAttempt -> TimeSpan.FromSeconds(Math.Pow(2.0, float retryAttempt))
        )

// Circuit breaker policy
let private circuitBreakerPolicy =
    HttpPolicyExtensions
        .HandleTransientHttpError()
        .CircuitBreakerAsync(
            handledEventsAllowedBeforeBreaking = 5,
            durationOfBreak = TimeSpan.FromSeconds(30.0)
        )

// Timeout policy
let private timeoutPolicy =
    Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(30.0))

// Combined policy
let private combinedPolicy =
    Policy.WrapAsync(retryPolicy, circuitBreakerPolicy, timeoutPolicy)

// Service provider for dependency injection
let mutable private serviceProvider: ServiceProvider option = None

// Initialize HTTP client factory
let initializeHttpClientFactory () =
    let services = ServiceCollection()

    services
        .AddHttpClient("telebot")
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
let private getHttpClient () =
    match serviceProvider with
    | Some provider ->
        let factory = provider.GetRequiredService<IHttpClientFactory>()
        factory.CreateClient("telebot")
    | None ->
        let provider = initializeHttpClientFactory()
        let factory = provider.GetRequiredService<IHttpClientFactory>()
        factory.CreateClient("telebot")

// Record HTTP metrics
let private recordHttpMetrics (method: string) (uri: Uri) (responseMessage: HttpResponseMessage) (duration: TimeSpan) (requestSize: int64 option) (responseSize: int64 option) =
    let host = uri.Host
    let statusCode = responseMessage.StatusCode.ToString()

    httpRequestsTotal.WithLabels([|method; host; statusCode|]).Inc()
    httpRequestDuration.WithLabels([|method; host|]).Observe(duration.TotalSeconds)

    requestSize |> Option.iter (fun size -> httpRequestSize.WithLabels([|method; host|]).Observe(float size))
    responseSize |> Option.iter (fun size -> httpResponseSize.WithLabels([|method; host|]).Observe(float size))

// Async HTTP request with telemetry
let private executeHttpRequestAsync (request: HttpRequestMessage) : Async<HttpResponseMessage> =
    async {
        use client = getHttpClient()
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:138.0) Gecko/20100101 Firefox/138.0")
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8")
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.5")
        client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document")
        client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate")
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none")
        client.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1")
        client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1")
        client.DefaultRequestHeaders.Add("DNT", "1")
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
let getAsync (url: string) : Async<HttpResponseMessage> =
    withOperationTelemetry "http_get" (fun scope ->
        async {
            TelemetryScope.addProperty "url" url scope |> ignore
            TelemetryScope.logInfo $"Making GET request to {url}" scope

            use request = new HttpRequestMessage(HttpMethod.Get, url)
            let! response = executeHttpRequestAsync request

            if response.IsSuccessStatusCode then
                TelemetryScope.logInfo $"GET request successful: {response.StatusCode}" scope
            else
                TelemetryScope.logWarning $"GET request failed: {response.StatusCode}" scope

            return response
        }
    )

// POST request with telemetry
let postAsync (url: string) (content: HttpContent) : Async<HttpResponseMessage> =
    withOperationTelemetry "http_post" (fun scope ->
        async {
            TelemetryScope.addProperty "url" url scope |> ignore
            TelemetryScope.logInfo $"Making POST request to {url}" scope

            use request = new HttpRequestMessage(HttpMethod.Post, url)
            request.Content <- content
            let! response = executeHttpRequestAsync request

            if response.IsSuccessStatusCode then
                TelemetryScope.logInfo $"POST request successful: {response.StatusCode}" scope
            else
                TelemetryScope.logWarning $"POST request failed: {response.StatusCode}" scope

            return response
        }
    )

// Execute custom request with telemetry
let executeRequestAsync (request: HttpRequestMessage) : Async<HttpResponseMessage> =
    withOperationTelemetry "http_custom" (fun scope ->
        async {
            let url = request.RequestUri.ToString()
            let method = request.Method.Method
            TelemetryScope.addProperty "url" url scope |> ignore
            TelemetryScope.addProperty "method" method scope |> ignore
            TelemetryScope.logInfo $"Making {method} request to {url}" scope

            let! response = executeHttpRequestAsync request

            if response.IsSuccessStatusCode then
                TelemetryScope.logInfo $"{method} request successful: {response.StatusCode}" scope
            else
                TelemetryScope.logWarning $"{method} request failed: {response.StatusCode}" scope

            return response
        }
    )

// Download data as byte array
let downloadBytesAsync (url: string) : Async<byte[]> =
    async {
        let! response = getAsync url
        response.EnsureSuccessStatusCode() |> ignore
        return! response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
    }

// Download data as string
let downloadStringAsync (url: string) : Async<string> =
    async {
        let! response = getAsync url
        response.EnsureSuccessStatusCode() |> ignore
        return! response.Content.ReadAsStringAsync() |> Async.AwaitTask
    }

// Health check function
let healthCheckAsync () : Async<bool> =
    async {
        try
            let! response = getAsync "https://httpbin.org/status/200"
            let isHealthy = response.IsSuccessStatusCode
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
