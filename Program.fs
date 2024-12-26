module Program

open System.IO;
open System.Text.RegularExpressions
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open dotenv.net
open Telebot.VideoDownloader
open Telebot.TikTok
open Telebot.Instagram

DotEnv.Load()

[<Literal>]
let replacementsFile = "replacements"

let readReplacements (filePath: string) =
    File.ReadAllLines filePath
    |> Array.map (fun line -> 
        let parts = line.Split('=')
        parts[0], parts[1])
    |> Map.ofArray

type Reply =
    | VideoFile of string
    | Message of string

let replacements: Map<string, string> = readReplacements replacementsFile

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

let reply (reply: Reply, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) =
    match reply with
    | VideoFile videoFile ->
        let file = InputFile.File(videoFile, new FileStream(videoFile, FileMode.Open, FileAccess.Read))
        Req.SendVideo.Make(chatId=chatId, video=file, replyParameters=ReplyParameters.Create(messageId.MessageId, chatId))
        |> api ctx.Config
        |> Async.Ignore
        |> Async.RunSynchronously
        deleteVideo videoFile
    | Message message ->
        Req.SendMessage.Make(chatId, message, replyParameters=ReplyParameters.Create(messageId.MessageId, chatId))
        |> api ctx.Config
        |> Async.Ignore
        |> Async.Start

let processVideos getLinks processVideo (messageText: string option, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) =
    let links = getLinks messageText
    links |> List.iter (fun link ->
        let video = processVideo link
        match video with
        | Some loadedVideoFile -> reply(VideoFile loadedVideoFile, messageId, chatId, ctx)
        | None -> ()
    )

let processTikTokVideos (messageText: string option, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) =
    processVideos getTikTokLinkIds (fun link -> processTikTokVideo (Some link)) (messageText, messageId, chatId, ctx)

let processInstagramLinks (messageText: string option, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) =
    processVideos getInstagramLinks (fun link -> processInstagramVideo link |> Async.RunSynchronously) (messageText, messageId, chatId, ctx)

let processReplacements (messageText: string option, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) =
    applyReplacements messageText |> List.iter (fun text -> reply(Message text, messageId, chatId, ctx))

let updateArrived (ctx: UpdateContext) =
  match ctx.Update.Message with
  | Some { MessageId = messageId; Chat = chat; Text = messageText } ->
    let mId = MessageId.Create(messageId)
    let cId = ChatId.Int(chat.Id)
    processTikTokVideos (messageText, mId, cId, ctx)
    processInstagramLinks (messageText, mId, cId, ctx)
    processReplacements (messageText, mId, cId, ctx)
  | _ -> ()

[<EntryPoint>]
let main _ =
  async {
    let config = Config.defaultConfig |> Config.withReadTokenFromEnv "TELEGRAM_BOT_TOKEN"
    let! _ = Api.deleteWebhookBase () |> api config
    return! startBot config updateArrived None
  } |> Async.RunSynchronously
  0