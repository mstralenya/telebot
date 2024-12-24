open System.IO
open System.Text.RegularExpressions
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Bot

[<Literal>]
let replacementsFile = "replacements"

let readReplacements (filePath: string) =
    File.ReadAllLines filePath
    |> Array.map (fun line -> 
        let parts = line.Split('=')
        parts.[0], parts.[1])
    |> Map.ofArray

let replacements: Map<string, string> = readReplacements replacementsFile

let applyReplacements (input: string option) =
    match input with
    | Some (text) ->
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
        |> List.rev
    | None -> List.Empty

let updateArrived (ctx: UpdateContext) =
  match ctx.Update.Message with
  | Some { MessageId = messageId; Chat = chat; Text = messageText } ->
    let replacementList = applyReplacements messageText
    replacementList |> List.iter (fun link ->
        Api.sendMessageReply chat.Id link messageId
        |> api ctx.Config
        |> Async.Ignore
        |> Async.Start)
  | _ -> ()

[<EntryPoint>]
let main _ =
  async {
    let config = Config.defaultConfig |> Config.withReadTokenFromEnv "TELEGRAM_BOT_TOKEN"
    let! _ = Api.deleteWebhookBase () |> api config
    return! startBot config updateArrived None
  } |> Async.RunSynchronously
  0