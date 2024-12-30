module Program

open System.IO;
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Telebot.Twitter
open Telebot.Youtube
open dotenv.net
open Telebot.Text
open Telebot.VideoDownloader
open Telebot.TikTok
open Telebot.Instagram

DotEnv.Load()

DotEnv.Load()

let reply (reply: Reply, messageId: MessageId, chatId: ChatId, ctx: UpdateContext) =
    match reply with
    | VideoFile (videoFile, replyText) ->
        let file = InputFile.File(videoFile, File.OpenRead(videoFile))
        Req.SendVideo.Make(chatId=chatId, video=file, caption=replyText, parseMode=ParseMode.HTML, replyParameters=ReplyParameters.Create(messageId.MessageId, chatId))
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
    messageText
    |> getLinks messageText
    |> List.iter (fun link ->
        processVideo link
        |> Option.iter (fun r -> reply(r, messageId, chatId, ctx))
    )

let processLinks getLinks processVideo = processVideos getLinks processVideo

let processTikTokVideos = processLinks getTikTokLinks getTikTokReply
let processInstagramLinks = processLinks getInstagramLinks getInstagramReply
let processTwitterLinks = processLinks getTwitterLinks getTwitterReply
let processYoutubeLinks = processLinks getYoutubeLinks getYoutubeReply

let updateArrived (ctx: UpdateContext) =
    match ctx.Update.Message with
    | Some { MessageId = messageId; Chat = chat; Text = messageText } ->
        let mId = MessageId.Create(messageId)
        let cId = ChatId.Int(chat.Id)
        [
            processTikTokVideos
            processInstagramLinks
            processTwitterLinks
            processYoutubeLinks
        ]
        |> List.iter (fun processMessage -> processMessage(messageText, mId, cId, ctx))
    | _ -> ()


[<EntryPoint>]
let main _ =
  async {
    let config = Config.defaultConfig |> Config.withReadTokenFromEnv "TELEGRAM_BOT_TOKEN"
    let! _ = Api.deleteWebhookBase () |> api config
    return! startBot config updateArrived None
  } |> Async.RunSynchronously
  0