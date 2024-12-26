module Telebot.Text

open System.IO;
open System.Text.RegularExpressions

[<Literal>]
let private replacementsFile = "replacements"

let private readReplacements (filePath: string) =
    File.ReadAllLines filePath
    |> Array.map (fun line -> 
        let parts = line.Split('=')
        parts[0], parts[1])
    |> Map.ofArray

type Reply =
    | VideoFile of string
    | Message of string

let private replacements: Map<string, string> = readReplacements replacementsFile

let applyReplacements (input: string option) =
    match input with
    | Some text ->
        replacements
        |> Map.fold (fun acc key value ->
            let matches = Regex.Matches(text, key)
            if matches.Count > 0 then
                let matched = matches
                              |> Seq.cast<Match>
                              |> Seq.toList
                              |> List.map (fun m -> Regex.Replace(m.Value, key, value))
                matched @ acc
            else
                acc) []
    | None -> List.Empty