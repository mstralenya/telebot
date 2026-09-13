module Telebot.Config

open System
open System.IO
open Serilog

let private getEnv name = Environment.GetEnvironmentVariable name

let private trimmed name =
    match getEnv name with
    | null -> None
    | value when String.IsNullOrWhiteSpace value -> None
    | value -> Some (value.Trim())

let private flag name =
    match getEnv name with
    | null -> false
    | value -> value.Equals("true", StringComparison.OrdinalIgnoreCase) || value = "1"

let private parseInt name =
    trimmed name
    |> Option.bind (fun value ->
        match Int32.TryParse value with true, parsed -> Some parsed | _ -> None)

/// Parses a comma/semicolon separated set of integer ids from an environment variable.
let internal parseIdSet (name: string) : Set<int64> =
    match getEnv name with
    | null -> Set.empty
    | value when String.IsNullOrWhiteSpace value -> Set.empty
    | value ->
        value.Split([|','; ';'|], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map _.Trim()
        |> Array.choose (fun s -> match Int64.TryParse s with true, id -> Some id | _ -> None)
        |> Set.ofArray

type AppConfig = {
    MetricsPort: int
    HealthPort: int
    WebAppBaseUrl: string option
    RedisConnectionString: string
    SqliteDbPath: string
    YoutubeCookiesPath: string option
    /// Maximum number of bus messages processed concurrently by this node
    MaxParallelMessages: int
    BlacklistedChatIds: Set<int64>
    BlacklistedUserIds: Set<int64>
    ProxyUrl: string option
    UseProxyForInstagramReels: bool
    UseProxyForInstagramPosts: bool
    UseProxyForTikTok: bool
    UseProxyForTwitter: bool
    UseProxyForYoutube: bool
    TwitterApiBase: string
    TwitterTranslationLang: string option
    LlmTranslationEnabled: bool
    LlmApiUrl: string option
    LlmApiType: string option
    LlmModel: string
    LlmSystemPromptTemplate: string option
    FfmpegVideoEncoder: string
    FfmpegVaapiDevice: string option
    MaxParallelVideoEncodes: int
}

let private defaultRedisConnectionString = "127.0.0.1:6379"
let private defaultSqliteDbPath = "data/telebot.db"
let private defaultTwitterApiBase = "https://api.fxtwitter.com/"
let private defaultLlmModel = "hf.co/jcbtc/CHADROCK3.6-35B-UNCENSORED-MTP-STRIX-LEAN:latest"

let private candidateSearchDirs () =
    [
        try Some (Directory.GetCurrentDirectory()) with _ -> None
        try Some AppContext.BaseDirectory with _ -> None
        try Some (Path.Combine(AppContext.BaseDirectory, "tools")) with _ -> None
    ]
    |> List.choose id

// Resolves a cookies file for yt-dlp: explicit env path first, then well-known file names
let private resolveYoutubeCookiesPath () =
    try
        match trimmed "YOUTUBE_COOKIES_PATH" with
        | Some envPath when File.Exists envPath ->
            Log.Information("Using cookies from environment variable YOUTUBE_COOKIES_PATH: {Path}", envPath)
            Some envPath
        | _ ->
            let cookieFiles = [ "youtube-cookies.txt"; "cookies.txt" ]
            let found =
                seq {
                    for dir in candidateSearchDirs () do
                        for file in cookieFiles do
                            yield Path.Combine(dir, file)
                }
                |> Seq.tryFind File.Exists

            match found with
            | Some path ->
                Log.Information("Using cookies file: {Path}", path)
                Some path
            | None ->
                Log.Information("No cookies file found for yt-dlp")
                None
    with ex ->
        Log.Error(ex, "Error resolving cookies for yt-dlp")
        None

let private load () : AppConfig =
    let twitterApiBase =
        match trimmed "TWITTER_API_BASE" with
        | Some apiBase -> if apiBase.EndsWith("/") then apiBase else apiBase + "/"
        | None -> defaultTwitterApiBase

    {
        MetricsPort = defaultArg (parseInt "METRICS_PORT") 3001
        HealthPort = defaultArg (parseInt "HEALTH_PORT") 3002
        WebAppBaseUrl = trimmed "WEBAPP_BASE_URL"
        RedisConnectionString =
            defaultArg (trimmed "REDIS_CONNECTION_STRING")
                (defaultArg (trimmed "REDIS_URL") defaultRedisConnectionString)
        SqliteDbPath = defaultArg (trimmed "SQLITE_DB_PATH") defaultSqliteDbPath
        YoutubeCookiesPath = resolveYoutubeCookiesPath ()
        MaxParallelMessages = defaultArg (parseInt "MAX_PARALLEL_MESSAGES") 5
        BlacklistedChatIds = parseIdSet "BLACKLIST_CHAT_IDS"
        BlacklistedUserIds = parseIdSet "BLACKLIST_USER_IDS"
        ProxyUrl = trimmed "PROXY_URL"
        UseProxyForInstagramReels = flag "USE_PROXY_FOR_INSTAGRAM_REELS"
        UseProxyForInstagramPosts = flag "USE_PROXY_FOR_INSTAGRAM_POSTS"
        UseProxyForTikTok = flag "USE_PROXY_FOR_TIKTOK"
        UseProxyForTwitter = flag "USE_PROXY_FOR_TWITTER"
        UseProxyForYoutube = flag "USE_PROXY_FOR_YOUTUBE"
        TwitterApiBase = twitterApiBase
        TwitterTranslationLang = trimmed "TWITTER_TRANSLATION_LANG"
        LlmTranslationEnabled = flag "LLM_TRANSLATION_ENABLED"
        LlmApiUrl = trimmed "LLM_API_URL"
        LlmApiType = trimmed "LLM_API_TYPE" |> Option.map _.ToLowerInvariant()
        LlmModel = defaultArg (trimmed "LLM_MODEL") defaultLlmModel
        LlmSystemPromptTemplate = trimmed "LLM_SYSTEM_PROMPT"
        FfmpegVideoEncoder = defaultArg (trimmed "FFMPEG_VIDEO_ENCODER") "libx264"
        FfmpegVaapiDevice = trimmed "FFMPEG_VAAPI_DEVICE"
        MaxParallelVideoEncodes = max 1 (defaultArg (parseInt "MAX_PARALLEL_VIDEO_ENCODES") 1)
    }

// Configuration is read once and shared; environment variables are not expected to change at runtime.
let private cached = lazy (load ())

/// Returns the application configuration, loaded once on first access.
let get () : AppConfig = cached.Value
