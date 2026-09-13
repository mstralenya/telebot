module Telebot.Handlers

open System.Diagnostics
open System.Threading.Tasks
open Funogram.Telegram
open Funogram.Telegram.Types
open Serilog
open Telebot.Bus
open Telebot.DataTypes
open Telebot.Messages
open Telebot.Replies
open Telebot.Policies
open Telebot.PrometheusMetrics

// Higher-order function to create specialized extractors
let createLinkExtractor (getLinks: string option -> string list) (mkMessage: string * UpdateMessage -> 'T) : UpdateMessage -> 'T list =
    fun msg -> msg.MessageText |> getLinks |> List.map (fun url -> mkMessage (url, msg))

// shape that is published afterwards.
// NOTE: must stay JSON-serializable over the bus transport — do not put
// abstract types (e.g. Telebot.Messages.Message) in here; carry the plain data instead.
type ProcessingResult =
    {
        Success: bool
        ElapsedMs: float
        Url: string
        OriginalMessage: UpdateMessage
        Reply: Reply option
    }

type BaseHandler() =
    /// Runs getReply with up to three attempts when exceptions occur.
    /// The outcome is always reported on the bus; a faulting operation is rethrown afterwards.
    member _.processLinkAsync (link: Message) (getReply: string -> Async<Reply option>) : Task =
        task {
            let sw = Stopwatch.StartNew()

            let! outcome =
                tryThreeTimesAsync (fun () ->
                    async {
                        match! getReply link.Url with
                        | Some reply -> return true, Some reply
                        | None -> return false, None
                    })
                |> Async.Catch
                |> Async.StartAsTask

            sw.Stop()

            let success, reply =
                match outcome with
                | Choice1Of2 (success, reply) -> success, reply
                | Choice2Of2 _ -> false, None

            let result = {
                Success = success
                ElapsedMs = sw.Elapsed.TotalMilliseconds
                Url = link.Url
                OriginalMessage = link.OriginalMessage
                Reply = reply
            }

            do! publishToBusAsync result |> Async.StartAsTask

            match outcome with
            | Choice2Of2 ex ->
                Log.Error(ex, "Handler failed for link {Url}", link.Url)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
            | Choice1Of2 _ -> ()
        }

type ResultHandler =
    new() = {  }
    member this.Handle(msg: ProcessingResult) : Task =
        task {
            // Log and record metrics
            processingTimeSummary.Observe msg.ElapsedMs

            let ctx = createUpdateContext()
            if msg.Success then
                Log.Debug $"Successfully processed link: {msg.Url}"
                // Send the reply if successful and there is one
                match msg.Reply with
                | Some r ->
                    do! replyAsync (r, msg.OriginalMessage.MessageId, msg.OriginalMessage.ChatId, ctx)
                        |> Async.StartAsTask
                | None -> ()
            else
                Log.Error $"Failed to process link: {msg.Url}"
                // Send error message
                let message =
                    Req.SendMessage.Make(
                        msg.OriginalMessage.ChatId,
                        "Failed to process link",
                        replyParameters =
                            ReplyParameters.Create(msg.OriginalMessage.MessageId.MessageId, msg.OriginalMessage.ChatId),
                        parseMode = ParseMode.HTML
                    )

                do! sendRequestAsync message ctx |> Async.StartAsTask
        }
