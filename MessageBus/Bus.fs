module Telebot.Bus

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Wolverine
open Serilog

// Global bus instance
let mutable private busHost: IHost option = None

// Initialize the bus
let initializeBus () =
    let host =
        Host
            .CreateDefaultBuilder()
            .UseWolverine(fun opts ->
                // Configure local queue
                opts
                    .LocalQueue("telebot")
                    .Sequential()
                    .MaximumParallelMessages(20)
                    .TelemetryEnabled true
                |> ignore
                opts.Policies.LogMessageStarting(LogLevel.Information);
                opts.PublishAllMessages().ToLocalQueue "telebot" |> ignore)
            .Build()

    host.Start()
    busHost <- Some host
    host

// Get the bus instance
let getBus () =
    match busHost with
    | Some host -> host.Services.GetRequiredService<IMessageBus>()
    | None ->
        let host = initializeBus ()
        host.Services.GetRequiredService<IMessageBus>()

// Send a message
let sendToBus<'T> (message: 'T) =
    task {
        try
            let bus = getBus ()
            do! bus.SendAsync message
        with
        | ex -> Log.Error(ex, "Error sending message")
    }
    |> Async.AwaitTask
    |> Async.RunSynchronously

// Publish a message
let publishToBus<'T> (message: 'T) =
    task {
        try
            let bus = getBus ()
            do! bus.PublishAsync message
        with
        | ex -> Log.Error(ex, "Error publishing message")
    }
    |> Async.AwaitTask
    |> Async.RunSynchronously

// Shutdown the bus
let shutdownBus () =
    match busHost with
    | Some host ->
        task {
            do! host.StopAsync()
            host.Dispose()
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously

        busHost <- None
    | None -> ()
