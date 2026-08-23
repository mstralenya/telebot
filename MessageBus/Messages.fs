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
    }


// Base class for link messages published to the bus.
// Concrete message types below are what handlers subscribe to.
[<AbstractClass>]
type Message(url: string, originalMessage: UpdateMessage) =
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

type InstagramAudioMessage(url, originalMessage) =
    inherit Message(url, originalMessage)

type InstagramShareMessage(url, originalMessage) =
    inherit Message(url, originalMessage)

type TwitterMessage(url, originalMessage) =
    inherit Message(url, originalMessage)

type YoutubeMessage(url, originalMessage) =
    inherit Message(url, originalMessage)

type YoutubeAudioMessage(url, originalMessage) =
    inherit Message(url, originalMessage)
