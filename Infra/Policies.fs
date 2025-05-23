module Telebot.Policies

open System
open System.IO
open System.Net.Http
open Polly
open Serilog

// Define a retry policy for transient HTTP errors
let private retryPolicy =
    Policy
        .Handle<HttpRequestException>() // Handle HTTP request exceptions
        .Or<IOException>()
        .OrResult<HttpResponseMessage>(fun (response: HttpResponseMessage) -> response.IsSuccessStatusCode = false)
        .WaitAndRetryAsync(
            retryCount = 3, // Retry 3 times
            sleepDurationProvider = fun retryAttempt -> TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) // Exponential backoff: 2^retryAttempt seconds
        )

// Define a bulkhead policy to limit concurrent executions
let private bulkheadPolicy =
    Policy.BulkheadAsync<HttpResponseMessage>(
        maxParallelization = 5, // Maximum of 5 concurrent executions
        maxQueuingActions = 10 // Maximum of 10 queued executions
    )

// Combine the retry and bulkhead policies
let private combinedPolicy = Policy.WrapAsync(bulkheadPolicy, retryPolicy)

// Expose a function to execute an async workflow with the combined policy
let executeWithPolicyAsync (asyncWorkflow: Async<HttpResponseMessage>) =
    combinedPolicy.ExecuteAsync(fun () -> asyncWorkflow |> Async.StartAsTask)
    |> Async.AwaitTask

// Async retry policy with better error handling and telemetry
let tryThreeTimesAsync<'T> (operation: unit -> Async<'T>) : Async<'T> =
    async {
        try
            return! operation()
        with
        | ex ->
            Log.Warning("Operation failed, retrying...")
            try
                do! Async.Sleep(1000)
                return! operation()
            with
            | ex2 ->
                Log.Warning("Operation failed on second attempt, retrying...")
                do! Async.Sleep(2000)
                return! operation()
    }

// Async message processing with retry policy
let tryThreeTimesAsyncWithTelemetry<'T> (operationName: string) (operation: unit -> Async<'T>) : Async<'T> =
    async {
        try
            return! operation()
        with
        | ex ->
            Log.Warning("Operation {OperationName} failed, retrying...", operationName)
            try
                do! Async.Sleep(1000)
                return! operation()
            with
            | ex2 ->
                Log.Warning("Operation {OperationName} failed on second attempt, retrying...", operationName)
                do! Async.Sleep(2000)
                return! operation()
    }

// Backward compatibility - synchronous version (discouraged)
let tryThreeTimes (processMessage: unit -> unit) =
    let asyncOperation () = async { processMessage() }
    tryThreeTimesAsync asyncOperation |> Async.RunSynchronously

// Circuit breaker for critical operations
let private circuitBreakerPolicy<'T> (operationName: string) =
    Policy
        .Handle<Exception>()
        .CircuitBreakerAsync(5, TimeSpan.FromMinutes(1.0))

// Execute operation with circuit breaker protection
let executeWithCircuitBreakerAsync<'T> (operationName: string) (operation: unit -> Async<'T>) : Async<'T> =
    async {
        let policy = circuitBreakerPolicy<'T> operationName
        let! result = policy.ExecuteAsync(fun () -> operation() |> Async.StartAsTask) |> Async.AwaitTask
        return result
    }

// Bulk operation with parallel processing and rate limiting
let executeBulkOperationAsync<'T, 'R> (items: 'T list) (operation: 'T -> Async<'R>) (maxParallel: int) : Async<'R[]> =
    async {
        Log.Information("Starting bulk operation with {ItemCount} items, max parallel: {MaxParallel}", items.Length, maxParallel)
        
        let semaphore = new System.Threading.SemaphoreSlim(maxParallel, maxParallel)
        
        let processItem item = async {
            do! Async.AwaitTask(semaphore.WaitAsync())
            try
                return! operation item
            finally
                semaphore.Release() |> ignore
        }
        
        let! results = items |> List.map processItem |> Async.Parallel
        
        Log.Information("Bulk operation completed, processed {ResultCount} items", results.Length)
        return results
    }

// Rate limited execution
let executeWithRateLimitAsync<'T> (maxOperationsPerSecond: int) (operation: unit -> Async<'T>) : Async<'T> =
    let intervalMs = 1000 / maxOperationsPerSecond
    async {
        do! Async.Sleep intervalMs
        return! operation()
    }

// Health check with timeout
let healthCheckWithTimeoutAsync (healthCheck: unit -> Async<bool>) (timeoutMs: int) : Async<bool> =
    async {
        try
            use cts = new System.Threading.CancellationTokenSource(timeoutMs)
            let! result = Async.StartChild(healthCheck(), timeoutMs)
            let! actualResult = result
            return actualResult
        with
        | :? TimeoutException ->
            Log.Warning("Health check timed out after {TimeoutMs}ms", timeoutMs)
            return false
        | ex ->
            Log.Error(ex, "Health check failed with exception")
            return false
    }

// Resource cleanup with retry
let cleanupWithRetryAsync (cleanup: unit -> Async<unit>) : Async<unit> =
    tryThreeTimesAsyncWithTelemetry "resource_cleanup" cleanup
