module Telebot.HttpClient

open System.Net.Http
open Telebot.LoggingHandler
open Telebot.Policies

module private Constants =
    let [<Literal>] UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3"

let client =
    let handler = new LoggingHandler(new HttpClientHandler())
    let c = new HttpClient(handler)
    c.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.UserAgent)
    c

let getAsync (url: string) =
    executeWithPolicyAsync (client.GetAsync(url) |> Async.AwaitTask) |> Async.RunSynchronously

let executeRequestAsync (request: HttpRequestMessage) : HttpResponseMessage =
    executeWithPolicyAsync (client.SendAsync request |> Async.AwaitTask) |> Async.RunSynchronously
