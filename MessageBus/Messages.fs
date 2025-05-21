module Telebot.Messages

open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Telebot.DataTypes
open Telebot.Text

// Base message type for Telegram updates
type UpdateMessage = {
    MessageText: string option
    MessageId: MessageId
    ChatId: ChatId
    Context: UpdateContext
}


// Different message types for specific link types
[<AbstractClass>]
type Message(url : string, originalMessage : UpdateMessage) =
    /// The public URL associated with the message.
    member _.Url : string = url
    /// The original incoming `UpdateMessage`.
    member _.OriginalMessage : UpdateMessage = originalMessage


type TikTokAudioMessage(url, originalMessage) =
    inherit Message (url, originalMessage)
type TikTokVideoMessage(url, originalMessage) =
    inherit Message (url, originalMessage)
type InstagramMessage(url, originalMessage) =
    inherit Message (url, originalMessage)
type InstagramShareMessage (url, originalMessage) =
    inherit Message (url, originalMessage)
type TwitterMessage (url, originalMessage) =
    inherit Message (url, originalMessage)
type YoutubeMessage (url, originalMessage) =
    inherit Message (url, originalMessage)


// Processing result message
type ProcessingResult = {
    Success: bool
    ElapsedMs: float
    Message: Message
    Reply: Reply option
}
