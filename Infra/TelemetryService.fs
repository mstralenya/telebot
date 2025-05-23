module Telebot.TelemetryService

open System
open System.Diagnostics
open Serilog
open Serilog.Context
open Telebot.PrometheusMetrics

// Activity source for distributed tracing
let private activitySource = new ActivitySource("Telebot")

// Telemetry context
type TelemetryContext = {
    CorrelationId: string
    UserId: string option
    ChatId: string option
    MessageId: string option
    OperationName: string
    Properties: Map<string, obj>
}

module TelemetryContext =
    let create operationName = {
        CorrelationId = Guid.NewGuid().ToString("N")
        UserId = None
        ChatId = None
        MessageId = None
        OperationName = operationName
        Properties = Map.empty
    }

    let withUserId userId ctx = { ctx with UserId = Some userId }
    let withChatId chatId ctx = { ctx with ChatId = Some chatId }
    let withMessageId messageId ctx = { ctx with MessageId = Some messageId }
    let withProperty key value ctx = { ctx with Properties = ctx.Properties |> Map.add key value }

// Scoped telemetry operations
type TelemetryScope = {
    Context: TelemetryContext
    Activity: Activity option
    StartTime: DateTimeOffset
    Stopwatch: Stopwatch
}

module TelemetryScope =
    let create (context: TelemetryContext) =
        let activity = activitySource.StartActivity(context.OperationName)
        let stopwatch = Stopwatch.StartNew()

        // Add context to activity
        match activity with
        | null -> ()
        | a ->
            a.SetTag("correlation_id", context.CorrelationId) |> ignore
            context.UserId |> Option.iter (fun id -> a.SetTag("user_id", id) |> ignore)
            context.ChatId |> Option.iter (fun id -> a.SetTag("chat_id", id) |> ignore)
            context.MessageId |> Option.iter (fun id -> a.SetTag("message_id", id) |> ignore)

            context.Properties |> Map.iter (fun k v -> a.SetTag(k, v.ToString()) |> ignore)

        {
            Context = context
            Activity = Option.ofObj activity
            StartTime = DateTimeOffset.UtcNow
            Stopwatch = stopwatch
        }

    let addProperty key value scope =
        scope.Activity |> Option.iter (fun a -> a.SetTag(key, value.ToString()) |> ignore)
        { scope with Context = scope.Context |> TelemetryContext.withProperty key value }

    let logInfo message scope =
        use _ = LogContext.PushProperty("CorrelationId", scope.Context.CorrelationId)
        use _ = LogContext.PushProperty("OperationName", scope.Context.OperationName)
        scope.Context.UserId |> Option.iter (fun id -> LogContext.PushProperty("UserId", id) |> ignore)
        scope.Context.ChatId |> Option.iter (fun id -> LogContext.PushProperty("ChatId", id) |> ignore)
        scope.Context.MessageId |> Option.iter (fun id -> LogContext.PushProperty("MessageId", id) |> ignore)

        Log.Information(message)

    let logWarning message scope =
        use _ = LogContext.PushProperty("CorrelationId", scope.Context.CorrelationId)
        use _ = LogContext.PushProperty("OperationName", scope.Context.OperationName)
        Log.Warning(message)

    let logError (ex: Exception option) message scope =
        use _ = LogContext.PushProperty("CorrelationId", scope.Context.CorrelationId)
        use _ = LogContext.PushProperty("OperationName", scope.Context.OperationName)

        match ex with
        | Some exn -> Log.Error(exn, message)
        | None -> Log.Error(message)

    let recordSuccess scope =
        scope.Activity |> Option.iter (fun a -> a.SetStatus(ActivityStatusCode.Ok) |> ignore)

        // Record timing metrics
        let elapsedMs = scope.Stopwatch.Elapsed.TotalMilliseconds
        processingTimeSummary.Observe(elapsedMs)

        scope

    let recordFailure (ex: Exception option) scope =
        scope.Activity |> Option.iter (fun a ->
            a.SetStatus(ActivityStatusCode.Error) |> ignore
            ex |> Option.iter (fun e -> a.SetTag("error.message", e.Message) |> ignore)
        )

        scope

    let complete scope =
        scope.Stopwatch.Stop()
        scope.Activity |> Option.iter (fun a -> a.Dispose())

// Higher-order function for scoped telemetry operations
let withTelemetry<'T> (context: TelemetryContext) (operation: TelemetryScope -> Async<'T>) : Async<'T> =
    async {
        let scope = TelemetryScope.create context

        try
            let! result = operation scope
            let _ = TelemetryScope.recordSuccess scope
            TelemetryScope.complete scope
            return result
        with
        | ex ->
            let _ = TelemetryScope.recordFailure (Some ex) scope |> TelemetryScope.logError (Some ex) "Operation failed"
            TelemetryScope.complete scope
            return raise ex
    }

// Convenience functions for common operations
let withMessageTelemetry chatId messageId operationName operation =
    let context =
        TelemetryContext.create operationName
        |> TelemetryContext.withChatId chatId
        |> TelemetryContext.withMessageId messageId

    withTelemetry context operation

let withOperationTelemetry operationName operation =
    let context = TelemetryContext.create operationName
    withTelemetry context operation

// Initialize telemetry
let initialize () =
    Log.Information("Telemetry service initialized")

// Cleanup telemetry
let shutdown () =
    activitySource.Dispose()
    Log.Information("Telemetry service shut down")
