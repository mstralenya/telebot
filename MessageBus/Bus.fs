module Telebot.Bus

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open System.Text.Json.Serialization
open Wolverine
open Wolverine.Redis
open Serilog
open Telebot.PrometheusMetrics
open Telebot.TelemetryService

// Global bus instance with proper async initialization
let mutable private busHost: IHost option = None
let private lockObj = obj()

let getRedisConnectionString () =
    let conn = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING")
    if String.IsNullOrWhiteSpace(conn) then
        let url = Environment.GetEnvironmentVariable("REDIS_URL")
        if String.IsNullOrWhiteSpace(url) then "127.0.0.1:6379" else url.Trim()
    else
        conn.Trim()

// Initialize the bus asynchronously
let initializeBusAsync () : Async<unit> =
    async {
        match busHost with
        | Some _ -> return ()
        | None ->
            let redisConn = getRedisConnectionString()
            Log.Information("Initializing message bus with persistent Redis transport ({RedisConn})...", redisConn)

            let host =
                Host
                    .CreateDefaultBuilder()
                    .UseWolverine(fun opts ->
                        // Configure System.Text.Json serializer options for F# types (Discriminated Unions, Records, Options)
                        opts.UseSystemTextJsonForSerialization(Action<System.Text.Json.JsonSerializerOptions>(fun jsonOpts ->
                            jsonOpts.Converters.Add(JsonFSharpConverter())
                        ))

                        // Configure Redis Transport for persistent message streams
                        opts.UseRedisTransport(redisConn).AutoProvision() |> ignore
                        opts.PublishAllMessages().ToRedisStream("telebot-messages") |> ignore
                        opts.ListenToRedisStream("telebot-messages", "telebot-consumer-group").Sequential() |> ignore
                        
                        // Configure logging and metrics
                        opts.Policies.LogMessageStarting(LogLevel.Information)
                    )
                    .Build()

            do! host.StartAsync() |> Async.AwaitTask
            busHost <- Some host
            
            Log.Information("Message bus initialized successfully with Redis transport")
            initializeApplicationMetrics()
    }

// Get the bus instance asynchronously
let getBusAsync () : Async<IMessageBus> =
    async {
        match busHost with
        | Some host -> 
            return host.Services.GetRequiredService<IMessageBus>()
        | None ->
            let! _ = initializeBusAsync ()
            return busHost.Value.Services.GetRequiredService<IMessageBus>()
    }

// Send a message asynchronously with telemetry
let sendToBusAsync<'T> (message: 'T) : Async<unit> =
    withOperationTelemetry "message_bus_send" (fun scope ->
        async {
            try
                TelemetryScope.addProperty "message_type" (typeof<'T>.Name) scope |> ignore
                TelemetryScope.logInfo $"Sending message of type {typeof<'T>.Name}" scope
                
                let! bus = getBusAsync ()
                do! bus.SendAsync message |> fun vt -> vt.AsTask() |> Async.AwaitTask
                
                messageBusProcessingRate.WithLabels([|"send"|]).Inc()
                TelemetryScope.logInfo "Message sent successfully" scope
            with
            | ex ->
                messageBusErrors.WithLabels([|"send_error"|]).Inc()
                TelemetryScope.logError (Some ex) "Error sending message" scope
                raise ex
        }
    )

// Publish a message asynchronously with telemetry
let publishToBusAsync<'T> (message: 'T) : Async<unit> =
    withOperationTelemetry "message_bus_publish" (fun scope ->
        async {
            try
                TelemetryScope.addProperty "message_type" (typeof<'T>.Name) scope |> ignore
                TelemetryScope.logInfo $"Publishing message of type {typeof<'T>.Name}" scope
                
                let! bus = getBusAsync ()
                do! bus.PublishAsync message |> fun vt -> vt.AsTask() |> Async.AwaitTask
                
                messageBusProcessingRate.WithLabels([|"publish"|]).Inc()
                TelemetryScope.logInfo "Message published successfully" scope
            with
            | ex ->
                messageBusErrors.WithLabels([|"publish_error"|]).Inc()
                TelemetryScope.logError (Some ex) "Error publishing message" scope
                raise ex
        }
    )

// Backward compatibility - synchronous versions (discouraged)
let sendToBus<'T> (message: 'T) : unit =
    sendToBusAsync message |> Async.RunSynchronously

let publishToBus<'T> (message: 'T) : unit =
    publishToBusAsync message |> Async.RunSynchronously

// Shutdown the bus asynchronously
let shutdownBusAsync () : Async<unit> =
    async {
        match busHost with
        | Some host ->
            try
                Log.Information("Shutting down message bus...")
                do! host.StopAsync() |> Async.AwaitTask
                host.Dispose()
                busHost <- None
                Log.Information("Message bus shut down successfully")
            with
            | ex ->
                Log.Error(ex, "Error shutting down message bus")
        | None -> 
            Log.Debug("Message bus was not initialized, nothing to shut down")
    }

// Backward compatibility - synchronous version
let shutdownBus () : unit =
    shutdownBusAsync () |> Async.RunSynchronously

// Health check for the message bus
let healthCheckAsync () : Async<bool> =
    async {
        try
            match busHost with
            | Some host ->
                let! bus = getBusAsync ()
                // Simple health check - try to get the bus service
                let isHealthy = not (isNull bus)
                healthCheckStatus.WithLabels([|"message_bus"|]).Set(if isHealthy then 1.0 else 0.0)
                return isHealthy
            | None ->
                healthCheckStatus.WithLabels([|"message_bus"|]).Set(0.0)
                return false
        with
        | ex ->
            Log.Error(ex, "Message bus health check failed")
            healthCheckStatus.WithLabels([|"message_bus"|]).Set(0.0)
            return false
    }

// Background task to monitor queue size (should be called periodically)
let updateQueueMetrics () : Async<unit> =
    async {
        try
            // This is a placeholder - actual implementation would depend on Wolverine's metrics API
            // For now, we'll just update the uptime
            updateUptimeMetric()
            updateMemoryMetrics()
            updateDiskSpaceMetrics()
        with
        | ex ->
            Log.Warning(ex, "Failed to update queue metrics")
    }

// Initialize the bus (convenience function)
let initializeBus () : unit =
    initializeBusAsync () |> Async.RunSynchronously
