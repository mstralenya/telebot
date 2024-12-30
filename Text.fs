module Telebot.Text

type Reply =
    | VideoFile of string * string
    | Message of string
