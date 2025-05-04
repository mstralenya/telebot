module Telebot.Policies

open System
open System.IO
open System.Net.Http
open Polly
open Serilog
open Telebot.PrometheusMetrics

// Define a retry policy for transient HTTP errors
let private retryPolicy =
    Policy
        .Handle<HttpRequestException>() // Handle HTTP request exceptions
        .Or<IOException>()
        .OrResult<HttpResponseMessage>(fun (response: HttpResponseMessage) -> response.IsSuccessStatusCode = false)
        .WaitAndRetryAsync(
            retryCount = 3,             // Retry 3 times
            sleepDurationProvider = fun retryAttempt -> 
                TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) // Exponential backoff: 2^retryAttempt seconds
        )

// Define a bulkhead policy to limit concurrent executions
let private bulkheadPolicy =
    Policy
        .BulkheadAsync<HttpResponseMessage>(
            maxParallelization = 5,     // Maximum of 5 concurrent executions
            maxQueuingActions = 10      // Maximum of 10 queued executions
        )

// Combine the retry and bulkhead policies
let private combinedPolicy = Policy.WrapAsync(bulkheadPolicy, retryPolicy)

// Expose a function to execute an async workflow with the combined policy
let executeWithPolicyAsync (asyncWorkflow: Async<HttpResponseMessage>) =
    combinedPolicy.ExecuteAsync(fun () -> asyncWorkflow |> Async.StartAsTask) |> Async.AwaitTask

let tryThreeTimes (processMessage: unit -> unit) =
    let rec tryWithRetries retriesLeft =
        try
            processMessage ()
            messageSuccessCounter.Inc() // Increment success counter
        with ex ->
            if retriesLeft > 0 then
                Log.Error(ex, $"Error occured, retries left: {retriesLeft}")
                tryWithRetries (retriesLeft - 1)
            else
                Log.Error(ex, "Error occurred. No more retries left.")
                messageFailureCounter.Inc() // Increment failure counter

    tryWithRetries 3