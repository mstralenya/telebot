module Telebot.Policies

open Serilog

/// Retries an async operation up to three times with increasing delays between attempts.
let tryThreeTimesAsync<'T> (operation: unit -> Async<'T>) : Async<'T> =
    async {
        try
            return! operation()
        with
        | _ ->
            Log.Warning("Operation failed, retrying...")
            try
                do! Async.Sleep(1000)
                return! operation()
            with
            | _ ->
                Log.Warning("Operation failed on second attempt, retrying...")
                do! Async.Sleep(2000)
                return! operation()
    }
