module Telebot.Blacklist

open System

// Helper to parse comma-separated IDs from an environment variable
let private parseIds (envVarName: string) : Set<int64> =
    let envVal = Environment.GetEnvironmentVariable(envVarName)
    if String.IsNullOrWhiteSpace(envVal) then
        Set.empty
    else
        envVal.Split([|','; ';'|], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun s -> s.Trim())
        |> Array.choose (fun s -> 
            match Int64.TryParse(s) with
            | true, v -> Some v
            | _ -> None
        )
        |> Set.ofArray

// Lazy initialization of blacklists so we parse only once
let private getBlacklistedChatIds = lazy (parseIds "BLACKLIST_CHAT_IDS")
let private getBlacklistedUserIds = lazy (parseIds "BLACKLIST_USER_IDS")

let isBlacklisted (chatId: int64) (userId: int64 option) : bool =
    let blacklistedChats = getBlacklistedChatIds.Value
    let blacklistedUsers = getBlacklistedUserIds.Value
    
    let isChatBlacklisted = blacklistedChats.Contains chatId
    let isUserBlacklisted = 
        match userId with
        | Some uid -> blacklistedUsers.Contains uid
        | None -> false
        
    isChatBlacklisted || isUserBlacklisted
