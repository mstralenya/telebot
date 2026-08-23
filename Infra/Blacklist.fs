module Telebot.Blacklist

open Telebot.Config

let isBlacklisted (chatId: int64) (userId: int64 option) : bool =
    let config = get ()

    let isChatBlacklisted = config.BlacklistedChatIds.Contains chatId
    let isUserBlacklisted =
        userId |> Option.map config.BlacklistedUserIds.Contains |> Option.defaultValue false

    isChatBlacklisted || isUserBlacklisted
