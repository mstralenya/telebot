namespace Telebot

open System
open System.Net.Http
open System.Text
open System.Text.Json
open Serilog
open Telebot.TwitterData

module Translation =

    // Configuration from environment variables
    let getLlmApiUrl () =
        let url = Environment.GetEnvironmentVariable("LLM_API_URL")
        if String.IsNullOrWhiteSpace(url) then None else Some (url.Trim())

    let getLlmModel () =
        let model = Environment.GetEnvironmentVariable("LLM_MODEL")
        if String.IsNullOrWhiteSpace(model) then 
            "hf.co/jcbtc/CHADROCK3.6-35B-UNCENSORED-MTP-STRIX-LEAN:latest"
        else 
            model.Trim()

    let getLlmSystemPrompt (targetLang: string) =
        let customPrompt = Environment.GetEnvironmentVariable("LLM_SYSTEM_PROMPT")
        if not (String.IsNullOrWhiteSpace(customPrompt)) then
            customPrompt.Replace("{lang}", targetLang)
        else
            $"You are a professional translator. Translate the given text to {targetLang} language as accurately as possible. Preserve the tone, meaning, formatting, emojis, and hashtags of the original text. Output ONLY the translated text without any explanation, intro, or markdown formatting. If the text is already in {targetLang}, return it exactly as-is."

    let getFullLanguageName (langCode: string) =
        match langCode.Trim().ToLowerInvariant() with
        | "en" -> "English"
        | "ru" -> "Russian"
        | "es" -> "Spanish"
        | "fr" -> "French"
        | "de" -> "German"
        | "it" -> "Italian"
        | "ja" -> "Japanese"
        | "zh" -> "Chinese"
        | "pt" -> "Portuguese"
        | "ko" -> "Korean"
        | "pl" -> "Polish"
        | "tr" -> "Turkish"
        | "uk" -> "Ukrainian"
        | code -> code.ToUpperInvariant()

    // JSON models for request/response
    type ChatMessage = {
        role: string
        content: string
    }

    type OpenAiChatRequest = {
        model: string
        messages: ChatMessage[]
        temperature: float
    }

    type OllamaOptions = {
        temperature: float
    }

    type OllamaChatRequest = {
        model: string
        messages: ChatMessage[]
        stream: bool
        options: OllamaOptions
    }

    type OllamaGenerateRequest = {
        model: string
        prompt: string
        system: string
        stream: bool
        options: OllamaOptions
    }

    type OpenAiChoiceMessage = {
        content: string
    }

    type OpenAiChoice = {
        message: OpenAiChoiceMessage
    }

    type OpenAiChatResponse = {
        choices: OpenAiChoice[]
    }

    type OllamaChatResponse = {
        message: ChatMessage
    }

    type OllamaGenerateResponse = {
        response: string
    }

    let private jsonOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    let getLlmApiType () =
        let apiType = Environment.GetEnvironmentVariable("LLM_API_TYPE")
        if String.IsNullOrWhiteSpace(apiType) then None else Some (apiType.Trim().ToLowerInvariant())

    let getLlmEndpointAndType (apiUrl: string) =
        let trimmed = apiUrl.Trim()
        let envType = getLlmApiType()
        
        match envType with
        | Some "openai" | Some "openai_compat" | Some "llama.cpp" | Some "rocmfpx" | Some "llama_cpp" ->
            let url =
                if trimmed.EndsWith("/v1") then trimmed + "/chat/completions"
                elif trimmed.EndsWith("/v1/") then trimmed + "chat/completions"
                elif trimmed.EndsWith("/chat/completions") then trimmed
                else 
                    let baseTrimmed = if trimmed.EndsWith("/") then trimmed else trimmed + "/"
                    if baseTrimmed.EndsWith("/v1/") then baseTrimmed + "chat/completions"
                    else baseTrimmed + "v1/chat/completions"
            url, "openai"
            
        | Some "ollama" | Some "ollama_chat" ->
            let url =
                if trimmed.EndsWith("/api") then trimmed + "/chat"
                elif trimmed.EndsWith("/api/") then trimmed + "chat"
                elif trimmed.EndsWith("/api/chat") then trimmed
                else 
                    let baseTrimmed = if trimmed.EndsWith("/") then trimmed else trimmed + "/"
                    if baseTrimmed.EndsWith("/api/") then baseTrimmed + "chat"
                    else baseTrimmed + "api/chat"
            url, "ollama_chat"
            
        | Some "ollama_generate" ->
            let url =
                if trimmed.EndsWith("/api") then trimmed + "/generate"
                elif trimmed.EndsWith("/api/") then trimmed + "generate"
                elif trimmed.EndsWith("/api/generate") then trimmed
                else 
                    let baseTrimmed = if trimmed.EndsWith("/") then trimmed else trimmed + "/"
                    if baseTrimmed.EndsWith("/api/") then baseTrimmed + "generate"
                    else baseTrimmed + "api/generate"
            url, "ollama_generate"
            
        | _ ->
            if trimmed.Contains("/v1") then
                let url =
                    if trimmed.EndsWith("/v1") then trimmed + "/chat/completions"
                    elif trimmed.EndsWith("/v1/") then trimmed + "chat/completions"
                    elif trimmed.EndsWith("/chat/completions") then trimmed
                    else trimmed
                url, "openai"
            else
                let url =
                    if trimmed.EndsWith("/api") then trimmed + "/chat"
                    elif trimmed.EndsWith("/api/") then trimmed + "chat"
                    elif trimmed.EndsWith("/api/chat") || trimmed.EndsWith("/api/generate") then trimmed
                    else 
                        let baseTrimmed = if trimmed.EndsWith("/") then trimmed else trimmed + "/"
                        baseTrimmed + "api/chat"
                let reqType = if url.EndsWith("/generate") then "ollama_generate" else "ollama_chat"
                url, reqType

    let translateTextAsync (text: string) (targetLang: string) : Async<TwitterTranslation option> =
        async {
            match getLlmApiUrl() with
            | None -> return None
            | Some apiUrl ->
                try
                    let model = getLlmModel()
                    let fullLang = getFullLanguageName targetLang
                    let systemPrompt = getLlmSystemPrompt fullLang
                    let endpoint, endpointType = getLlmEndpointAndType apiUrl
                    
                    Log.Information("Translating text using LLM ({Model}) via {EndpointType} endpoint at {Endpoint}", model, endpointType, endpoint)
                    
                    use client = new HttpClient()
                    client.Timeout <- TimeSpan.FromSeconds(120.0)
                    
                    let jsonContent =
                        match endpointType with
                        | "openai" ->
                            let req = {
                                model = model
                                messages = [|
                                    { role = "system"; content = systemPrompt }
                                    { role = "user"; content = text }
                                |]
                                temperature = 0.3
                            }
                            JsonSerializer.Serialize(req, jsonOptions)
                        | "ollama_chat" ->
                            let req = {
                                model = model
                                messages = [|
                                    { role = "system"; content = systemPrompt }
                                    { role = "user"; content = text }
                                |]
                                stream = false
                                options = { temperature = 0.3 }
                            }
                            JsonSerializer.Serialize(req, jsonOptions)
                        | "ollama_generate" ->
                            let req = {
                                model = model
                                prompt = text
                                system = systemPrompt
                                stream = false
                                options = { temperature = 0.3 }
                            }
                            JsonSerializer.Serialize(req, jsonOptions)
                        | _ -> failwith $"Unsupported endpoint type: {endpointType}"
                        
                    use content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
                    let! response = client.PostAsync(endpoint, content) |> Async.AwaitTask
                    
                    if not response.IsSuccessStatusCode then
                        let! errContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                        Log.Warning("LLM translation API returned status code {StatusCode}: {Error}", response.StatusCode, errContent)
                        return None
                    else
                        let! resBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                        
                        let translatedText =
                            match endpointType with
                            | "openai" ->
                                let res = JsonSerializer.Deserialize<OpenAiChatResponse>(resBody, jsonOptions)
                                if res.choices <> null && res.choices.Length > 0 then
                                    Some res.choices.[0].message.content
                                else
                                    None
                            | "ollama_chat" ->
                                let res = JsonSerializer.Deserialize<OllamaChatResponse>(resBody, jsonOptions)
                                if res.message.content <> null then
                                    Some res.message.content
                                else
                                    None
                            | "ollama_generate" ->
                                let res = JsonSerializer.Deserialize<OllamaGenerateResponse>(resBody, jsonOptions)
                                if res.response <> null then
                                    Some res.response
                                else
                                    None
                            | _ -> None
                            
                        match translatedText with
                        | Some t when not (String.IsNullOrWhiteSpace(t)) ->
                            let trimmedTranslation = t.Trim()
                            Log.Information("Successfully translated text with LLM. Length: {Length}", trimmedTranslation.Length)
                            return Some {
                                text = trimmedTranslation
                                source_language = "LLM"
                                destination_language = targetLang
                            }
                        | _ ->
                            Log.Warning("LLM translation returned empty text")
                            return None
                with
                | ex ->
                    Log.Error(ex, "Error translating text via LLM")
                    return None
        }
