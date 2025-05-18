module Program

open System
open System.IO
open System.Threading
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Serilog
open Telebot.LoggingHandler
open Telebot.Policies
open Telebot.PrometheusMetrics
open Telebot.Reply
open Prometheus
open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful

let updateArrived (ctx: UpdateContext) =
    match ctx.Update.Message with
    | Some {
               MessageId = messageId
               Chat = chat
               Text = messageText
           } ->
        newMessageCounter.Inc() // Increment new message counter
        let mId = MessageId.Create messageId
        let cId = ChatId.Int chat.Id

        [
            processTikTokVideos
            processInstagramLinks
            processInstagramShareLinks
            processTwitterLinks
            processYoutubeLinks
        ]
        |> List.iter (fun processMessage ->
            let stopwatch = System.Diagnostics.Stopwatch.StartNew()
            let mutable isSuccess = false

            try
                tryThreeTimes (fun () ->
                    processMessage (messageText, mId, cId, ctx)
                    isSuccess <- true)
            finally
                stopwatch.Stop()

                match isSuccess with
                | true -> Log.Debug $"Successfully processed message {messageText}"
                | false ->
                    let message =
                        Req.SendMessage.Make(
                            cId,
                            "Failed to process link",
                            replyParameters = ReplyParameters.Create(messageId, cId),
                            parseMode = ParseMode.HTML
                        )

                    sendRequestAsync message ctx |> Async.RunSynchronously

                processingTimeSummary.Observe(stopwatch.Elapsed.TotalMilliseconds))
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
    Async.Start(server)

[<EntryPoint>]
let main _ =
    Console.OutputEncoding <- Text.Encoding.UTF8
    Log.Logger <- LoggerConfiguration().WriteTo.Console().CreateLogger()

    // Start the metrics server
    let metricsPort =
        Environment.GetEnvironmentVariable("METRICS_PORT")
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
        Log.Information "Bot stopped"
        Log.CloseAndFlush()

    Log.CloseAndFlush()
    0
