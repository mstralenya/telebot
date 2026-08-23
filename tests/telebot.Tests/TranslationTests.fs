module Telebot.Tests.TranslationTests

open Xunit
open Telebot.Translation

let private withEnvType envType url =
    resolveEndpoint envType url

[<Theory>]
[<InlineData("openai", "https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")>]
[<InlineData("openai_compat", "https://api.openai.com/v1/", "https://api.openai.com/v1/chat/completions")>]
[<InlineData("llama_cpp", "https://host/api.openai.com/v1/chat/completions", "https://host/api.openai.com/v1/chat/completions")>]
[<InlineData("llama.cpp", "http://localhost:8080", "http://localhost:8080/v1/chat/completions")>]
let ``openai-style types resolve to chat completions`` (envType, input, expected) =
    let url, kind = withEnvType (Some envType) input
    Assert.Equal(expected, url)
    Assert.Equal("openai", kind)

[<Theory>]
[<InlineData("ollama", "http://localhost:11434", "http://localhost:11434/api/chat")>]
[<InlineData("ollama_chat", "http://localhost:11434/", "http://localhost:11434/api/chat")>]
[<InlineData("ollama_chat", "http://localhost:11434/api", "http://localhost:11434/api/chat")>]
[<InlineData("ollama_chat", "http://localhost:11434/api/", "http://localhost:11434/api/chat")>]
[<InlineData("ollama_chat", "http://localhost:11434/api/chat", "http://localhost:11434/api/chat")>]
let ``ollama chat types resolve to api chat`` (envType, input, expected) =
    let url, kind = withEnvType (Some envType) input
    Assert.Equal(expected, url)
    Assert.Equal("ollama_chat", kind)

[<Theory>]
[<InlineData("http://localhost:11434/api/generate")>]
[<InlineData("http://localhost:11434/api")>]
let ``ollama_generate type resolves to generate endpoint`` (input) =
    let url, kind = withEnvType (Some "ollama_generate") input
    Assert.Equal("http://localhost:11434/api/generate", url)
    Assert.Equal("ollama_generate", kind)

[<Theory>]
[<InlineData("https://api.example.com/v1", "https://api.example.com/v1/chat/completions", "openai")>]
[<InlineData("http://localhost:11434", "http://localhost:11434/api/chat", "ollama_chat")>]
[<InlineData("http://localhost:11434/api/generate", "http://localhost:11434/api/generate", "ollama_generate")>]
let ``without a type hint the endpoint is auto-detected`` (input, expectedUrl, expectedKind) =
    let url, kind = withEnvType None input
    Assert.Equal(expectedUrl, url)
    Assert.Equal(expectedKind, kind)

[<Theory>]
[<InlineData("Hello", "Hello")>]
[<InlineData("<think>reasoning</think>Answer", "Answer")>]
[<InlineData("Before<think>hidden</think>After", "BeforeAfter")>]
[<InlineData("<think>only thinking</think>", "")>]
[<InlineData("", "")>]
let ``cleanThinkingTags strips reasoning blocks`` (input, expected) =
    Assert.Equal(expected, cleanThinkingTags input)

[<Theory>]
[<InlineData("Привет, как дела?", true)>]
[<InlineData("The quick brown fox jumps over the lazy dog", true)>]
[<InlineData("just ok", true)>]
[<InlineData("Hallo, wie geht es dir heute?", false)>]
[<InlineData("Bonjour mes amis", false)>]
[<InlineData("12345 !@#", true)>]
let ``isRussianOrEnglish classifies texts`` (input, expected) =
    Assert.Equal(expected, isRussianOrEnglish input)
