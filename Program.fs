module Program

open System
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Serilog
open Telebot.Policies
open Telebot.Reply

let updateArrived (ctx: UpdateContext) =
    match ctx.Update.Message with
    | Some { MessageId = messageId
             Chat = chat
             Text = messageText } ->
        let mId = MessageId.Create(messageId)
        let cId = ChatId.Int(chat.Id)

        [ processTikTokVideos
          processInstagramLinks
          processInstagramShareLinks
          processTwitterLinks
          processYoutubeLinks ]
        |> List.iter (fun processMessage -> tryThreeTimes (fun () -> processMessage (messageText, mId, cId, ctx)))
    | _ -> ()

type SerilogLogger() =
    interface Funogram.Types.IBotLogger with
        member _.Log(text) = Log.Information(text)
        member _.Enabled = true

[<EntryPoint>]
let main _ =
    Console.OutputEncoding <- Text.Encoding.UTF8
    Log.Logger <- LoggerConfiguration().WriteTo.Console().CreateLogger()

    async {
        let config =
            Config.defaultConfig |> Config.withReadTokenFromEnv "TELEGRAM_BOT_TOKEN"

        let config =
            { config with
                RequestLogger = Some(SerilogLogger()) }

        let! _ = Api.deleteWebhookBase () |> api config
        return! startBot config updateArrived None
    }
    |> Async.RunSynchronously

    Log.CloseAndFlush()
    0
