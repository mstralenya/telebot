module Telebot.Messages

open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Telebot.DataTypes
open Wolverine.Attributes

// Base message type for Telegram updates
type UpdateMessage =
    {
        MessageText: string option
        MessageId: MessageId
        ChatId: ChatId
        Context: UpdateContext
    }


// Different message types for specific link types
type Message(url: string, originalMessage: UpdateMessage) =
    do 
        if typeof<Message>.IsAssignableFrom(typedefof<Message>) && 
           obj.ReferenceEquals(typeof<Message>, _.GetType()) then
            invalidOp "Message class should not be instantiated directly"
    /// The public URL associated with the message.
    [<Audit>]
    member _.Url: string = url
    /// The original incoming `UpdateMessage`.
    member _.OriginalMessage: UpdateMessage = originalMessage


type TikTokAudioMessage(url, originalMessage) =
    inherit Message(url, originalMessage)

type TikTokVideoMessage(url, originalMessage) =
    inherit Message(url, originalMessage)

type InstagramMessage(url, originalMessage) =
    inherit Message(url, originalMessage)

type InstagramShareMessage(url, originalMessage) =
    inherit Message(url, originalMessage)

type TwitterMessage(url, originalMessage) =
    inherit Message(url, originalMessage)

type YoutubeMessage(url, originalMessage) =
    inherit Message(url, originalMessage)


// Processing result message
type ProcessingResult =
    {
        Success: bool
        ElapsedMs: float
        Message: Message
        Reply: Reply option
    }
