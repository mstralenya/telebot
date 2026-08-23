module Telebot.WebServer

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Serilog
open Prometheus
open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful
open Telebot.Bus
open Telebot.PrometheusMetrics

// Global cancellation token source for graceful shutdown
let cancellationTokenSource = new CancellationTokenSource()

// Health monitoring background service
type HealthMonitoringService() =
    inherit BackgroundService()

    override this.ExecuteAsync(stoppingToken: CancellationToken) =
        async {
            while not stoppingToken.IsCancellationRequested do
                try
                    // Update various metrics
                    do! updateQueueMetrics()

                    // Perform health checks
                    let! httpHealthy = HttpClient.healthCheckAsync()
                    let! busHealthy = healthCheckAsync()

                    // Overall health status
                    let overallHealth = httpHealthy && busHealthy
                    healthCheckStatus.WithLabels([|"overall"|]).Set(if overallHealth then 1.0 else 0.0)

                    // Wait 30 seconds before next check
                    do! Async.Sleep(30000)
                with
                | ex ->
                    Log.Error(ex, "Error in health monitoring service")
                    do! Async.Sleep(5000) // Shorter retry interval on error
        } |> Async.StartAsTask :> Task

// Prometheus metrics endpoint with async support
let prometheusEndpoint =
    let metricsAsync (ctx: HttpContext) =
        async {
            try
                use stream = new MemoryStream()
                let registry = Metrics.DefaultRegistry
                do! registry.CollectAndExportAsTextAsync(stream, cancellationTokenSource.Token) |> Async.AwaitTask
                stream.Position <- 0
                use reader = new StreamReader(stream)
                let! content = reader.ReadToEndAsync() |> Async.AwaitTask
                return! OK content ctx
            with
            | ex ->
                Log.Error(ex, "Error generating metrics")
                return! ServerErrors.INTERNAL_ERROR "Error generating metrics" ctx
        }

    path "/metrics" >=> Writers.setMimeType "text/plain" >=> metricsAsync

let generateWebAppHtml (contentHtml: string) =
    $"""<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0, user-scalable=no">
    <title>Original Tweet Text</title>
    <script src="https://telegram.org/js/telegram-web-app.js"></script>
    <style>
        :root {{
            --bg-color: var(--tg-theme-bg-color, #18181b);
            --text-color: var(--tg-theme-text-color, #f4f4f5);
            --hint-color: var(--tg-theme-hint-color, #a1a1aa);
            --btn-color: var(--tg-theme-button-color, #3b82f6);
            --btn-text: var(--tg-theme-button-text-color, #ffffff);
            --card-bg: var(--tg-theme-secondary-bg-color, #27272a);
        }}
        body {{
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
            background-color: var(--bg-color);
            color: var(--text-color);
            margin: 0;
            padding: 20px;
            box-sizing: border-box;
            display: flex;
            flex-direction: column;
            min-height: 100vh;
        }}
        .header {{
            display: flex;
            align-items: center;
            justify-content: space-between;
            margin-bottom: 16px;
            padding-bottom: 12px;
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
        }}
        .title {{
            font-size: 1.1rem;
            font-weight: 600;
            margin: 0;
            display: flex;
            align-items: center;
            gap: 8px;
        }}
        .badge {{
            background: var(--btn-color);
            color: var(--btn-text);
            font-size: 0.75rem;
            padding: 2px 8px;
            border-radius: 12px;
            font-weight: 500;
        }}
        .content {{
            background: var(--card-bg);
            border-radius: 16px;
            padding: 16px;
            line-height: 1.6;
            font-size: 1rem;
            box-shadow: 0 4px 20px rgba(0, 0, 0, 0.15);
            border: 1px solid rgba(255, 255, 255, 0.05);
            word-break: break-word;
            flex-grow: 1;
        }}
        blockquote {{
            margin: 12px 0;
            padding: 8px 12px;
            border-left: 3px solid var(--btn-color);
            background: rgba(255, 255, 255, 0.03);
            border-radius: 0 8px 8px 0;
        }}
        a {{
            color: var(--btn-color);
            text-decoration: none;
        }}
        .close-btn {{
            margin-top: 20px;
            width: 100%%;
            padding: 14px;
            background-color: var(--btn-color);
            color: var(--btn-text);
            border: none;
            border-radius: 12px;
            font-size: 1rem;
            font-weight: 600;
            cursor: pointer;
            transition: opacity 0.2s;
        }}
        .close-btn:active {{
            opacity: 0.8;
        }}
    </style>
</head>
<body>
    <div class="header">
        <h1 class="title">Original Tweet <span class="badge">Original</span></h1>
    </div>
    <div class="content">
        {contentHtml}
    </div>
    <button class="close-btn" onclick="Telegram.WebApp.close()">Close</button>
    <script>
        Telegram.WebApp.ready();
        Telegram.WebApp.expand();
    </script>
</body>
</html>"""

let webAppHandler (ctx: HttpContext) =
    async {
        let cacheId = ctx.request.queryParam "id"
        match cacheId with
        | Choice1Of2 id ->
            match Translation.tryGetTranslationFromCache id with
            | Some cached ->
                let html = generateWebAppHtml cached.OriginalText
                return! (OK html >=> Writers.setMimeType "text/html; charset=utf-8") ctx
            | None ->
                let html = generateWebAppHtml "<i>Original text not found or expired.</i>"
                return! (OK html >=> Writers.setMimeType "text/html; charset=utf-8") ctx
        | _ ->
            let html = generateWebAppHtml "<i>Invalid request.</i>"
            return! (OK html >=> Writers.setMimeType "text/html; charset=utf-8") ctx
    }

// Health check and WebApp endpoint
let healthEndpoint =
    let healthCheckAsync (ctx: HttpContext) =
        async {
            try
                let! httpHealthy = HttpClient.healthCheckAsync()
                let! busHealthy = healthCheckAsync()

                let overall = httpHealthy && busHealthy
                let status = if overall then "healthy" else "unhealthy"
                let statusCode = if overall then OK else ServerErrors.SERVICE_UNAVAILABLE

                let checks = JsonObject()
                checks["http_client"] <- JsonValue.Create(if httpHealthy then "healthy" else "unhealthy")
                checks["message_bus"] <- JsonValue.Create(if busHealthy then "healthy" else "unhealthy")

                let healthData = JsonObject()
                healthData["status"] <- JsonValue.Create status
                healthData["timestamp"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("O"))
                healthData["checks"] <- checks

                return! statusCode (healthData.ToJsonString()) ctx
            with
            | ex ->
                Log.Error(ex, "Health check failed")
                return! ServerErrors.INTERNAL_ERROR "Health check failed" ctx
        }

    choose [
        path "/health" >=> Writers.setMimeType "application/json" >=> healthCheckAsync
        path "/webapp" >=> webAppHandler
    ]

// Start web server asynchronously
let startWebServerAsync (port: int) (app: WebPart) : Async<unit> =
    async {
        let config = {
            defaultConfig with
                bindings = [ HttpBinding.createSimple HTTP "0.0.0.0" port ]
                cancellationToken = cancellationTokenSource.Token
        }

        Log.Information($"Starting web server on port {port}")

        // Start the server in the background - don't wait for it
        async {
            startWebServer config app
            return ()
        } |> Async.Start

        Log.Information($"Web server started successfully on port {port}")
    }
