module Program

open System
open System.IO
open System.Threading
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Serilog
open Telebot.Bus
open Telebot.LoggingHandler
open Telebot.Messages
open Telebot.PrometheusMetrics
open Prometheus
open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful

let updateArrived (ctx: UpdateContext) =
    match ctx.Update.Message with
    | Some { MessageId = messageId; Chat = chat; Text = messageText } ->
        newMessageCounter.Inc() // Increment new message counter
        let mId = MessageId.Create messageId
        let cId = ChatId.Int chat.Id

        // Create update message
        let updateMessage = {
            MessageText = messageText
            MessageId = mId
            ChatId = cId
            Context = ctx
        }

        sendToBus updateMessage

    | _ -> ()

let prometheusEndpoint =
    let metrics (ctx: HttpContext) =
        async {
            use stream = new MemoryStream()
            let registry = Metrics.DefaultRegistry
            do! Async.AwaitTask(registry.CollectAndExportAsTextAsync(stream, CancellationToken.None))
            stream.Position <- 0
            use reader = new StreamReader(stream)
            let! content = Async.AwaitTask(reader.ReadToEndAsync())
            return! OK content ctx
        }

    path "/metrics" >=> Writers.setMimeType "text/plain" >=> metrics

let startWebServer port =
    let config =
        { defaultConfig with
            bindings = [ HttpBinding.createSimple HTTP "0.0.0.0" port ]
        }

    let app = choose [ prometheusEndpoint ]
    let _, server = startWebServerAsync config app
    Async.Start server

[<EntryPoint>]
let main _ =
    Console.OutputEncoding <- Text.Encoding.UTF8
    Log.Logger <- LoggerConfiguration().WriteTo.Console().CreateLogger()

    // Initialize the message bus
    initializeBus() |> ignore

    // Start the metrics server
    let metricsPort =
        Environment.GetEnvironmentVariable "METRICS_PORT"
        |> Option.ofObj
        |> Option.map int
        |> Option.defaultValue 9090

    Log.Information $"Starting metrics server on port {metricsPort}..."
    startWebServer metricsPort

    try
        async {
            let config =
                Config.defaultConfig |> Config.withReadTokenFromEnv "TELEGRAM_BOT_TOKEN"

            let config =
                { config with
                    RequestLogger = Some(SerilogLogger())
                }

            let! _ = Api.deleteWebhookBase () |> api config
            Log.Information "starting bot..."
            return! startBot config updateArrived None
        }
        |> Async.RunSynchronously
    finally
        Log.Information "Stopping bot..."
        // Shutdown the bus
        shutdownBus()
        Log.Information "Bot stopped"
        Log.CloseAndFlush()

    Log.CloseAndFlush()
    0
