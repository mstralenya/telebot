module Telebot.LoggingHandler

open System.Text.RegularExpressions
open Serilog

/// Routes Funogram API request logs through Serilog.
type SerilogLogger() =
    interface Funogram.Types.IBotLogger with
        member _.Log text =
            try
                Log.Information(Regex.Unescape text)
            with _ ->
                Log.Information text
        member _.Enabled = true
