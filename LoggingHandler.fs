module Telebot.LoggingHandler

open System.Net.Http
open System.Threading

type LoggingHandler(innerHandler: HttpMessageHandler) =
    inherit DelegatingHandler(innerHandler)

    override _.SendAsync(request: HttpRequestMessage, cancellationToken: CancellationToken) =
        // Log the request
        printfn $"Request: %s{request.Method.Method} %s{request.RequestUri.ToString()}"
        if request.Content <> null then
            task {
                let! content = request.Content.ReadAsStringAsync()
                printfn $"Request Content: %s{content}"
            } |> ignore

        base.SendAsync(request, cancellationToken)