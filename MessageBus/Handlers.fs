module Telebot.Handlers

open System.Diagnostics
open System.Threading.Tasks
open Funogram.Telegram
open Funogram.Telegram.Types
open Serilog
open Telebot.Bus
open Telebot.DataTypes
open Telebot.Messages
open Telebot.Text
open Telebot.Policies
open Telebot.PrometheusMetrics

// Generic link extractor function
let extractLinks<'T>
    (linkExtractor: string option -> string list)
    (messageConstructor: string -> UpdateMessage -> 'T)
    (message: UpdateMessage)
    : 'T list =
    linkExtractor message.MessageText
    |> List.map (fun url -> messageConstructor url message)

// Higher-order function to create specialized extractors
let createLinkExtractor (getLinks: string option -> string list) (mkMessage: string * UpdateMessage -> 'T) : UpdateMessage -> 'T list =
    fun msg -> msg.MessageText |> getLinks |> List.map (fun url -> mkMessage (url, msg))

// shape that is published afterwards
type ProcessingResult =
    {
        Success: bool
        ElapsedMs: float
        Link: Message
        Reply: Reply option
    }

//create abstract class BaseHandler with processLink method
type BaseHandler() =
    member this.processLink (link: Message) (getReply: string -> Reply option) : Task =
        task {
            let sw = Stopwatch.StartNew()
            let mutable ok = false
            let mutable rep = None

            try
                tryThreeTimes (fun () ->
                    match getReply link.Url with
                    | Some r ->
                        ok <- true
                        rep <- Some r
                    | None -> ok <- false)
            finally
                sw.Stop()

                let result =
                    {
                        Success = ok
                        ElapsedMs = sw.Elapsed.TotalMilliseconds
                        Link = link
                        Reply = rep
                    }

                publishToBus result
        }

type ResultHandler =
    // Add parameterless constructor
    new() = {  }
    // Process result handler
    member this.Handle(msg: ProcessingResult) : Task =
        task {
            // Log and record metrics
            processingTimeSummary.Observe msg.ElapsedMs

            if msg.Success then
                Log.Debug $"Successfully processed link: {msg.Link.Url}"
                // Send the reply if successful and there is one
                match msg.Reply with
                | Some r -> reply (r, msg.Link.OriginalMessage.MessageId, msg.Link.OriginalMessage.ChatId, msg.Link.OriginalMessage.Context)
                | None -> ()
            else
                Log.Error $"Failed to process link: {msg.Link.Url}"
                // Send error message
                let message =
                    Req.SendMessage.Make(
                        msg.Link.OriginalMessage.ChatId,
                        "Failed to process link",
                        replyParameters =
                            ReplyParameters.Create(msg.Link.OriginalMessage.MessageId.MessageId, msg.Link.OriginalMessage.ChatId),
                        parseMode = ParseMode.HTML
                    )

                sendRequestAsync message msg.Link.OriginalMessage.Context
                |> Async.RunSynchronously
        }
