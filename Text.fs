module Telebot.Text

open System.IO
open System.Text.RegularExpressions

[<Literal>]
let private replacementsFile = "replacements"

let private readReplacements filePath =
    File.ReadAllLines filePath
    |> Array.map (fun line -> let parts = line.Split('=') in parts[0], parts[1])
    |> Map.ofArray

type Reply =
    | VideoFile of string
    | Message of string

let private replacements = readReplacements replacementsFile

let applyReplacements input =
    input
    |> Option.map (fun text ->
        replacements
        |> Map.fold (fun acc key value ->
            Regex.Matches(text, key)
            |> Seq.cast<Match>
            |> Seq.map (fun m -> Regex.Replace(m.Value, key, value))
            |> Seq.toList
            |> List.append acc) [])
    |> Option.defaultValue List.empty