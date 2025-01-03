module Telebot.Seq

// Add helper function at the top of the file
let tryMaxBy (projection: 'T -> 'U) (source: seq<'T>) =
    if Seq.isEmpty source then None
    else Some(Seq.maxBy projection source)

