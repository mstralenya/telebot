namespace Telebot

open System
open System.Net.Http
open System.Text
open System.Text.Json
open Microsoft.Data.Sqlite
open Serilog
open Telebot.TwitterData

module Translation =

    // Configuration from Telebot.Config
    let getLlmApiUrl () = Config.get().LlmApiUrl

    let getLlmModel () = Config.get().LlmModel

    let getLlmSystemPrompt (targetLang: string) =
        match Config.get().LlmSystemPromptTemplate with
        | Some customPrompt -> customPrompt.Replace("{lang}", targetLang)
        | None ->
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
        max_tokens: int option
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

    let internal getLlmApiType () = Config.get().LlmApiType

    /// Pure endpoint resolution: maps an API base url + optional type hint to a concrete endpoint.
    let internal resolveEndpoint (envType: string option) (apiUrl: string) =
        let trimmed = apiUrl.Trim()

        // Joins the base url with a path segment, normalizing trailing slashes
        let join (suffix: string) =
            if trimmed.EndsWith("/") then trimmed + suffix else trimmed + "/" + suffix

        // Resolves <root> + <leaf> endpoints, e.g. "/v1" + "chat/completions"
        let resolve (root: string) (leaf: string) (full: string) =
            if trimmed.EndsWith(full) then trimmed
            elif trimmed.EndsWith($"{root}/") then trimmed + leaf
            elif trimmed.EndsWith(root) then $"{trimmed}/{leaf}"
            else join $"{root.TrimStart('/')}/{leaf}"

        match envType with
        | Some "openai" | Some "openai_compat" | Some "llama.cpp" | Some "rocmfpx" | Some "llama_cpp" ->
            resolve "/v1" "chat/completions" "/v1/chat/completions", "openai"
        | Some "ollama" | Some "ollama_chat" ->
            resolve "/api" "chat" "/api/chat", "ollama_chat"
        | Some "ollama_generate" ->
            resolve "/api" "generate" "/api/generate", "ollama_generate"
        | _ ->
            if trimmed.Contains("/v1") then
                // Best-effort openai-compatible resolution without rewriting unknown paths
                let url =
                    if trimmed.EndsWith("/chat/completions") then trimmed
                    elif trimmed.EndsWith("/v1/") then trimmed + "chat/completions"
                    elif trimmed.EndsWith("/v1") then trimmed + "/chat/completions"
                    else trimmed
                url, "openai"
            else
                let url =
                    if trimmed.EndsWith("/api/chat") || trimmed.EndsWith("/api/generate") then trimmed
                    elif trimmed.EndsWith("/api/") then trimmed + "chat"
                    elif trimmed.EndsWith("/api") then $"{trimmed}/chat"
                    else join "api/chat"
                let reqType = if url.EndsWith("/generate") then "ollama_generate" else "ollama_chat"
                url, reqType

    let getLlmEndpointAndType (apiUrl: string) =
        resolveEndpoint (getLlmApiType ()) apiUrl

    let internal cleanThinkingTags (text: string) =
        if String.IsNullOrWhiteSpace(text) then text
        else
            let regex = Text.RegularExpressions.Regex(@"<think>[\s\S]*?</think>", Text.RegularExpressions.RegexOptions.IgnoreCase)
            let cleaned = regex.Replace(text, "").Trim()
            if String.IsNullOrWhiteSpace(cleaned) && text.Contains("</think>") then
                let idx = text.LastIndexOf("</think>")
                text.Substring(idx + 8).Trim()
            elif String.IsNullOrWhiteSpace(cleaned) then
                text.Trim()
            else
                cleaned

    let internal isRussianOrEnglish (text: string) =
        let mutable cyrillic = 0
        let mutable latin = 0
        let mutable totalLetters = 0
        
        for i = 0 to text.Length - 1 do
            let c = text.[i]
            if Char.IsLetter(c) then
                totalLetters <- totalLetters + 1
                if c >= '\u0400' && c <= '\u04FF' then cyrillic <- cyrillic + 1
                elif (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') then latin <- latin + 1
        
        if totalLetters = 0 then true
        else
            let cyrRatio = float cyrillic / float totalLetters
            let latRatio = float latin / float totalLetters
            
            if cyrRatio > 0.4 then true
            elif latRatio > 0.7 then
                let words = text.ToLowerInvariant().Split([|' '; '\n'; '\r'; '\t'; '.'; ','; '!'; '?'; '"'; '\''; '('; ')'; '-'; '_'|], StringSplitOptions.RemoveEmptyEntries)
                let englishWords = set ["the"; "be"; "to"; "of"; "and"; "a"; "in"; "that"; "have"; "i"; "it"; "for"; "not"; "on"; "with"; "he"; "as"; "you"; "do"; "at"; "this"; "but"; "his"; "by"; "from"; "they"; "we"; "say"; "her"; "she"; "or"; "an"; "will"; "my"; "one"; "all"; "would"; "there"; "their"; "what"; "so"; "up"; "out"; "if"; "about"; "who"; "get"; "which"; "go"; "me"; "is"; "are"; "was"; "were"; "can"; "like"; "just"; "don't"; "im"; "i'm"; "it's"]
                let englishCount = words |> Array.filter englishWords.Contains |> Array.length
                
                if words.Length > 0 then
                    (float englishCount / float words.Length) >= 0.05 || englishCount >= 2
                else true
            else false

    let private chatMessages systemPrompt text =
        [|
            { role = "system"; content = systemPrompt }
            { role = "user"; content = text }
        |]

    let private serializeRequest model systemPrompt text endpointType =
        match endpointType with
        | "openai" ->
            JsonSerializer.Serialize(
                { model = model; messages = chatMessages systemPrompt text; temperature = 0.3; max_tokens = Some 1000 },
                jsonOptions)
        | "ollama_chat" ->
            JsonSerializer.Serialize(
                { model = model; messages = chatMessages systemPrompt text; stream = false; options = { temperature = 0.3 } },
                jsonOptions)
        | "ollama_generate" ->
            JsonSerializer.Serialize(
                { model = model; prompt = text; system = systemPrompt; stream = false; options = { temperature = 0.3 } },
                jsonOptions)
        | other -> failwith $"Unsupported endpoint type: {other}"

    let private extractTranslatedText (resBody: string) endpointType =
        match endpointType with
        | "openai" ->
            let res = JsonSerializer.Deserialize<OpenAiChatResponse>(resBody, jsonOptions)
            if res.choices <> null && res.choices.Length > 0 then Some res.choices.[0].message.content else None
        | "ollama_chat" ->
            let res = JsonSerializer.Deserialize<OllamaChatResponse>(resBody, jsonOptions)
            if res.message.content <> null then Some res.message.content else None
        | "ollama_generate" ->
            let res = JsonSerializer.Deserialize<OllamaGenerateResponse>(resBody, jsonOptions)
            if res.response <> null then Some res.response else None
        | _ -> None

    let translateTextAsync (text: string) (targetLang: string) : Async<TwitterTranslation option> =
        async {
            if isRussianOrEnglish text then return None
            else
            match getLlmApiUrl() with
            | None -> return None
            | Some apiUrl ->
                try
                    let model = getLlmModel()
                    let fullLang = getFullLanguageName targetLang
                    let systemPrompt = getLlmSystemPrompt fullLang
                    let endpoint, endpointType = getLlmEndpointAndType apiUrl

                    Log.Information("Translating text using LLM ({Model}) via {EndpointType} endpoint at {Endpoint}", model, endpointType, endpoint)

                    use content = new StringContent(serializeRequest model systemPrompt text endpointType, Encoding.UTF8, "application/json")
                    let client = Telebot.HttpClient.createLlmClient ()
                    let! response = client.PostAsync(endpoint, content) |> Async.AwaitTask

                    if not response.IsSuccessStatusCode then
                        let! errContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                        Log.Warning("LLM translation API returned status code {StatusCode}: {Error}", response.StatusCode, errContent)
                        return None
                    else
                        let! resBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                        match extractTranslatedText resBody endpointType with
                        | Some t when not (String.IsNullOrWhiteSpace(t)) ->
                            let trimmedTranslation = cleanThinkingTags t
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

    type CachedTranslation = {
        OriginalText: string
        TranslatedText: string
    }

    let getSqliteDbPath () = Config.get().SqliteDbPath

    /// How long cached translations stay retrievable (matches the "expired" wording shown to users)
    let internal cacheTtlDays = 7
    let private ttlModifier = $"-{cacheTtlDays} days"

    let initDb () =
        let dbPath = getSqliteDbPath ()
        let dir = System.IO.Path.GetDirectoryName(dbPath)
        if not (String.IsNullOrWhiteSpace(dir)) && not (System.IO.Directory.Exists(dir)) then
            System.IO.Directory.CreateDirectory(dir) |> ignore

        let connectionString = sprintf "Data Source=%s" dbPath
        use conn = new SqliteConnection(connectionString)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            CREATE TABLE IF NOT EXISTS translation_cache (
                key TEXT PRIMARY KEY,
                original_text TEXT NOT NULL,
                translated_text TEXT NOT NULL,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );
        """
        cmd.ExecuteNonQuery() |> ignore
        Log.Information("SQLite translation cache database initialized at {DbPath}", dbPath)

    let saveTranslationToCache (original: string) (translated: string) : string =
        try
            let key = Guid.NewGuid().ToString("N")
            let dbPath = getSqliteDbPath ()
            let connectionString = sprintf "Data Source=%s" dbPath
            use conn = new SqliteConnection(connectionString)
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "INSERT INTO translation_cache (key, original_text, translated_text) VALUES (@key, @orig, @trans); DELETE FROM translation_cache WHERE created_at < datetime('now', @ttl);"
            cmd.Parameters.AddWithValue("@key", key) |> ignore
            cmd.Parameters.AddWithValue("@orig", original) |> ignore
            cmd.Parameters.AddWithValue("@trans", translated) |> ignore
            cmd.Parameters.AddWithValue("@ttl", ttlModifier) |> ignore
            cmd.ExecuteNonQuery() |> ignore
            key
        with ex ->
            Log.Error(ex, "Error saving translation to SQLite cache")
            Guid.NewGuid().ToString("N")

    let tryGetTranslationFromCache (key: string) : CachedTranslation option =
        try
            let dbPath = getSqliteDbPath ()
            let connectionString = sprintf "Data Source=%s" dbPath
            use conn = new SqliteConnection(connectionString)
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT original_text, translated_text FROM translation_cache WHERE key = @key AND created_at >= datetime('now', @ttl) LIMIT 1;"
            cmd.Parameters.AddWithValue("@key", key) |> ignore
            cmd.Parameters.AddWithValue("@ttl", ttlModifier) |> ignore
            use reader = cmd.ExecuteReader()
            if reader.Read() then
                let orig = reader.GetString(0)
                let trans = reader.GetString(1)
                Some { OriginalText = orig; TranslatedText = trans }
            else
                None
        with ex ->
            Log.Error(ex, "Error reading translation from SQLite cache key {Key}", key)
            None
