module Program

open System
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Bot
open Serilog
open Telebot.Bus
open Telebot.LoggingHandler
open Telebot.PrometheusMetrics
open Telebot.TelemetryService
open Telebot.UpdateHandler
open Telebot.WebServer

// Configure structured logging
let configureLogging () =
    Log.Logger <-
        LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithCorrelationId()
            .Enrich.WithEnvironmentName()
            .Enrich.WithProcessId()
            .Enrich.WithThreadId()
            .WriteTo.Console(outputTemplate =
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} " +
                "{Properties:j}{NewLine}{Exception}")
            .CreateLogger()

    Log.Information("Structured logging configured")

// Graceful shutdown handler
let setupGracefulShutdown () =
    let shutdown () =
        async {
            Log.Information("Initiating graceful shutdown...")

            try
                try
                    // Cancel all background operations
                    cancellationTokenSource.Cancel()

                    // Shutdown services in reverse order
                    do! shutdownBusAsync()
                    Telebot.HttpClient.cleanup()
                    shutdown()

                    Log.Information("Graceful shutdown completed")
                with
                | ex -> Log.Error(ex, "Error during shutdown")
            finally
                Log.CloseAndFlush()
        }

    // Handle console cancel events
    Console.CancelKeyPress.Add(fun _ ->
        shutdown() |> Async.RunSynchronously
        Environment.Exit(0)
    )

    // Handle process termination
    AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
        shutdown() |> Async.RunSynchronously
    )

// Create and configure host with dependency injection
let createHost () =
    Host.CreateDefaultBuilder()
        .ConfigureServices(fun context services ->
            // Add HTTP client factory
            Telebot.HttpClient.initializeHttpClientFactory() |> ignore

            // Add background services
            services.AddHostedService<HealthMonitoringService>() |> ignore
        )
        .Build()

// Main async entry point
let mainAsync () : Async<int> =
    async {
        try
            // Set console encoding
            Console.OutputEncoding <- Text.Encoding.UTF8

            // Configure logging first
            configureLogging()

            // Initialize telemetry
            initialize()

            // Setup graceful shutdown
            setupGracefulShutdown()

            Log.Information("Starting Telebot application...")

            return! withOperationTelemetry "application_startup" (fun scope ->
                async {
                    try
                        // Validate required environment variables first
                        let botToken = Environment.GetEnvironmentVariable "TELEGRAM_BOT_TOKEN"
                        if String.IsNullOrWhiteSpace botToken then
                            failwith "TELEGRAM_BOT_TOKEN environment variable is required but not set. Please set this environment variable before running the application."

                        // Initialize SQLite translation database
                        Telebot.Translation.initDb()

                        // Initialize metrics
                        initializeApplicationMetrics()

                        // Initialize the message bus
                        let! _ = initializeBusAsync()
                        TelemetryScope.logInfo "Message bus initialized" scope

                        // Create and start the host for background services
                        use host = createHost()
                        do! host.StartAsync(cancellationTokenSource.Token) |> Async.AwaitTask
                        TelemetryScope.logInfo "Background services started" scope

                        // Start the metrics and health web servers on separate ports
                        let metricsPort = Telebot.Config.get().MetricsPort
                        let healthPort = Telebot.Config.get().HealthPort

                        do! startWebServerAsync metricsPort prometheusEndpoint
                        do! startWebServerAsync healthPort healthEndpoint
                        TelemetryScope.logInfo $"Metrics server started on port {metricsPort}, Health server started on port {healthPort}" scope

                        // Configure and start the Telegram bot
                        let config =
                            Config.defaultConfig
                            |> Config.withReadTokenFromEnv "TELEGRAM_BOT_TOKEN"

                        let configWithLogger = {
                            config with RequestLogger = Some(SerilogLogger())
                        }

                        // Remove webhook and start polling
                        let! _ = Api.deleteWebhookBase () |> api configWithLogger
                        TelemetryScope.logInfo "Webhook removed, starting bot polling" scope

                        // Start bot with enhanced async handler
                        do! startBot configWithLogger (updateArrivedAsync >> Async.RunSynchronously) None

                        return 0 // Success
                    with
                    | ex ->
                        TelemetryScope.logError (Some ex) "Application startup failed" scope
                        return 1
                }
            )
        with
        | ex ->
            Log.Fatal(ex, "Critical error during application startup")
            return 1
    }

[<EntryPoint>]
let main _ =
    let result =
        try
            mainAsync() |> Async.RunSynchronously
        finally
            // Ensure cleanup even if main fails
            cancellationTokenSource.Cancel()
            Log.CloseAndFlush()

    result
