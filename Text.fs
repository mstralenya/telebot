module Telebot.Text

open System.Text.RegularExpressions

type Reply =
    | VideoFile of file: string * caption: string option * thumbnail: string option
    | Message of string

let getLinks (regex: string) (text: string option) =
    text
    |> Option.map (fun text ->
        Regex.Matches(text, regex)
        |> Seq.cast<Match>
        |> Seq.map (_.Value)
        |> Seq.toList)
    |> Option.defaultValue List.empty