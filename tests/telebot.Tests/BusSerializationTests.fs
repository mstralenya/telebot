module Telebot.Tests.BusSerializationTests

open System.Text.Json
open Xunit
open Funogram.Telegram.Types
open Telebot.DataTypes
open Telebot.Handlers
// JsonFSharpConverter lives here since FSharp.SystemTextJson 1.4
open System.Text.Json.Serialization
// Telebot.Messages.Message must win over Funogram.Telegram.Types.Message
open Telebot.Messages

// Mirrors the serializer configuration used for the Wolverine bus transport in Bus.fs
let private busOptions () =
    let options = JsonSerializerOptions()
    options.Converters.Add(JsonFSharpConverter())
    options

[<Fact>]
let ``ProcessingResult survives a bus serialization round trip`` () =
    let original =
        { Success = true
          ElapsedMs = 123.4
          Url = "https://x.com/alice/status/123"
          OriginalMessage =
            { MessageText = Some "check this out"
              MessageId = MessageId.Create 42L
              ChatId = ChatId.Int -100123L }
          Reply = Some (Reply.Message ("<b>hello</b>", None)) }

    let options = busOptions ()
    let json = JsonSerializer.Serialize(original, options)
    let deserialized = JsonSerializer.Deserialize<ProcessingResult>(json, options)

    Assert.Equal(original.Success, deserialized.Success)
    Assert.Equal(original.ElapsedMs, deserialized.ElapsedMs)
    Assert.Equal(original.Url, deserialized.Url)
    Assert.Equal(original.Reply, deserialized.Reply)
    Assert.Equal(Some "check this out", deserialized.OriginalMessage.MessageText)

[<Fact>]
let ``UpdateMessage with failed result survives a bus round trip`` () =
    let original =
        { Success = false
          ElapsedMs = 5.0
          Url = "https://www.instagram.com/reel/abc/"
          OriginalMessage =
            { MessageText = None
              MessageId = MessageId.Create 7L
              ChatId = ChatId.String "-100" }
          Reply = None }

    let options = busOptions ()
    let json = JsonSerializer.Serialize(original, options)
    let deserialized = JsonSerializer.Deserialize<ProcessingResult>(json, options)

    Assert.False(deserialized.Success)
    Assert.Equal(None, deserialized.OriginalMessage.MessageText)
    Assert.Equal(None, deserialized.Reply)

[<Fact>]
let ``Concrete link messages survive a bus serialization round trip`` () =
    let updateMessage =
        { MessageText = Some "look"
          MessageId = MessageId.Create 9L
          ChatId = ChatId.Int 555L }

    let original = InstagramMessage("https://www.instagram.com/p/xyz/", updateMessage)
    let options = busOptions ()

    let json = let typed = original :> Telebot.Messages.Message in JsonSerializer.Serialize(typed, options)
    let deserialized = JsonSerializer.Deserialize<InstagramMessage>(json, options)

    Assert.Equal("https://www.instagram.com/p/xyz/", deserialized.Url)
    Assert.Equal(updateMessage, deserialized.OriginalMessage)
