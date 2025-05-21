module Telebot.Helpers

open System.Threading.Tasks

// Helper function to convert ValueTask<T> to Task<T>
let toTask (valueTask: ValueTask<'T>) = valueTask.AsTask()

// Add helper function at the top of the file
let tryMaxBy (projection: 'T -> 'U) (source: seq<'T>) =
    if Seq.isEmpty source then
        None
    else
        Some(Seq.maxBy projection source)
