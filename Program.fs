module Program

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Serilog
open Telebot.Bus
open Telebot.LoggingHandler
open Telebot.Messages
open Telebot.PrometheusMetrics
open Telebot.TelemetryService
open Prometheus
open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful

// Global cancellation token source for graceful shutdown
let private cancellationTokenSource = new CancellationTokenSource()

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
                    let! httpHealthy = Telebot.HttpClient.healthCheckAsync()
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

// Enhanced update handler with telemetry
let updateArrivedAsync (ctx: UpdateContext) : Async<unit> =
    async {
        match ctx.Update.Message with
        | Some {
                   MessageId = messageId
                   Chat = chat
                   Text = messageText
                   From = user
               } ->
            // Check blacklist
            let userId = user |> Option.map (fun u -> u.Id)
            if Telebot.Blacklist.isBlacklisted chat.Id userId then
               Log.Information($"Message ignored due to blacklist. ChatId: {chat.Id}, UserId: {userId}")
               return ()
            else

            // Create telemetry context
            let chatIdStr = chat.Id.ToString()
            let messageIdStr = messageId.ToString()
            let userIdStr = user |> Option.map (fun u -> u.Id.ToString())

            return! withMessageTelemetry chatIdStr messageIdStr "process_telegram_message" (fun scope ->
                async {
                    // Add user information to telemetry
                    userIdStr |> Option.iter (fun id -> TelemetryScope.addProperty "user_id" id scope |> ignore)
                    TelemetryScope.addProperty "chat_type" (chat.Type.ToString()) scope |> ignore
                    messageText |> Option.iter (fun text ->
                        TelemetryScope.addProperty "message_length" text.Length scope |> ignore
                        TelemetryScope.addProperty "has_text" true scope |> ignore
                    )

                    TelemetryScope.logInfo $"Processing message {messageId} from chat {chat.Id}" scope

                    // Increment metrics
                    newMessageCounter.Inc()

                    // Create message data
                    let mId = MessageId.Create messageId
                    let cId = ChatId.Int chat.Id

                    let updateMessage = {
                        MessageText = messageText
                        MessageId = mId
                        ChatId = cId
                        Context = ctx
                    }

                    // Send to bus asynchronously
                    do! sendToBusAsync updateMessage

                    TelemetryScope.logInfo "Message processed and sent to bus successfully" scope
                }
            )
        | _ ->
            return ()
    }

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

// Health check endpoint
let healthEndpoint =
    let healthCheckAsync (ctx: HttpContext) =
        async {
            try
                let! httpHealthy = Telebot.HttpClient.healthCheckAsync()
                let! busHealthy = healthCheckAsync()

                let overall = httpHealthy && busHealthy
                let status = if overall then "healthy" else "unhealthy"
                let statusCode = if overall then OK else ServerErrors.SERVICE_UNAVAILABLE

                let healthData = {|
                    status = status
                    timestamp = DateTimeOffset.UtcNow.ToString("O")
                    checks = {|
                        http_client = if httpHealthy then "healthy" else "unhealthy"
                        message_bus = if busHealthy then "healthy" else "unhealthy"
                    |}
                |}

                return! statusCode (Newtonsoft.Json.JsonConvert.SerializeObject(healthData)) ctx
            with
            | ex ->
                Log.Error(ex, "Health check failed")
                return! ServerErrors.INTERNAL_ERROR "Health check failed" ctx
        }

    path "/health" >=> Writers.setMimeType "application/json" >=> healthCheckAsync

// Start web server asynchronously
let startWebServerAsync (port: int) : Async<unit> =
    async {
        let config = {
            defaultConfig with
                bindings = [ HttpBinding.createSimple HTTP "0.0.0.0" port ]
                cancellationToken = cancellationTokenSource.Token
        }

        let app = choose [ prometheusEndpoint; healthEndpoint ]

        Log.Information($"Starting web server on port {port}")

        // Start the server in the background - don't wait for it
        async {
            startWebServer config app
            return ()
        } |> Async.Start

        Log.Information($"Web server started successfully on port {port}")
    }

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

                        // Initialize metrics
                        initializeApplicationMetrics()

                        // Initialize the message bus
                        let! _ = initializeBusAsync()
                        TelemetryScope.logInfo "Message bus initialized" scope

                        // Create and start the host for background services
                        use host = createHost()
                        do! host.StartAsync(cancellationTokenSource.Token) |> Async.AwaitTask
                        TelemetryScope.logInfo "Background services started" scope

                        // Start the metrics/health web server
                        let metricsPort =
                            Environment.GetEnvironmentVariable "METRICS_PORT"
                            |> Option.ofObj
                            |> Option.map int
                            |> Option.defaultValue 3001

                        do! startWebServerAsync metricsPort
                        TelemetryScope.logInfo $"Metrics server started on port {metricsPort}" scope

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
