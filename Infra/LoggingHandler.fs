module Telebot.LoggingHandler

open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Serilog

type SerilogLogger() =
    interface Funogram.Types.IBotLogger with
        member _.Log text = Log.Information text
        member _.Enabled = true

type LoggingHandler(innerHandler: HttpMessageHandler) =
    inherit DelegatingHandler(innerHandler)

    override _.SendAsync(request: HttpRequestMessage, cancellationToken: CancellationToken) : Task<HttpResponseMessage> =
        // Log request
        printfn $"Request: {request}"
        let task = ``base``.SendAsync(request, cancellationToken)

        task.ContinueWith (fun (t: Task<HttpResponseMessage>) ->
            let response = t.Result
            // Log response
            printfn $"Response: {response}"
            response)
