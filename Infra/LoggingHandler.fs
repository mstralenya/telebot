module Telebot.LoggingHandler

open System.Text.RegularExpressions
open Serilog

let private telegramTokenPattern =
    Regex(@"(?i)(api\.telegram\.org/bot)[^/\s]+", RegexOptions.Compiled)

let internal redactSensitiveText (text: string) =
    telegramTokenPattern.Replace(text, "$1<redacted>")

/// Routes Funogram API request logs through Serilog.
type SerilogLogger() =
    interface Funogram.Types.IBotLogger with
        member _.Log text =
            let decoded =
                try Regex.Unescape text
                with _ -> text

            Log.Information(redactSensitiveText decoded)
        member _.Enabled = true
