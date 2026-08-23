module Telebot.Tests.YoutubeTests

open System
open System.Text.Json.Nodes
open Xunit
open Telebot.Youtube.Youtube

let private formatJson (formatId: string) (ext: string) (vcodec: string) (acodec: string) : JsonNode =
    JsonNode.Parse $"""{{ "format_id": "%s{formatId}", "ext": "%s{ext}", "vcodec": "%s{vcodec}", "acodec": "%s{acodec}", "tbr": 1000.0, "abr": null, "vbr": null, "filesize": null, "filesize_approx": null, "width": 1920, "height": 1080 }}"""

let private videoOnly id ext vcodec = formatJson id ext vcodec "none"
let private audioOnly id ext acodec = formatJson id ext "none" acodec

let private formatsOf (tokens: JsonNode list) =
    let array = JsonArray()
    for token in tokens do
        array.Add(token) |> ignore
    let obj = JsonObject()
    obj["formats"] <- array
    obj

[<Fact>]
let ``parseFormats reads basic fields`` () =
    let json = JsonNode.Parse """{ "formats": [ { "format_id": "18", "ext": "mp4", "vcodec": "avc1.42001E", "acodec": "mp4a.40.2", "height": 360, "filesize_approx": 12345678 } ] }"""
    let formats = parseFormats json
    Assert.Single(formats) |> ignore
    let f = formats.[0]
    Assert.Equal("18", f.format_id)
    Assert.Equal(Some "mp4", f.ext)
    Assert.Equal(Some "avc1.42001E", f.vcodec)
    Assert.Equal(Some 360, f.height)
    Assert.Equal(Some 12345678L, f.filesize_approx)

[<Fact>]
let ``parseFormats maps language fallbacks`` () =
    let json = JsonNode.Parse """{ "formats": [
        { "format_id": "a1", "vcodec": "none", "acodec": "mp4a", "language": "en" },
        { "format_id": "a2", "vcodec": "none", "acodec": "mp4a", "lang": "de" },
        { "format_id": "a3", "vcodec": "none", "acodec": "mp4a", "audio_lang": "fr" },
        { "format_id": "a4", "vcodec": "none", "acodec": "mp4a", "audio_track": { "id": "at1", "name": "Dubbed" } }
    ] }"""
    let formats = parseFormats json
    Assert.Equal(4, formats.Length)
    Assert.Equal(Some "en", formats.[0].language)
    Assert.Equal(Some "de", formats.[1].language)
    Assert.Equal(Some "fr", formats.[2].language)
    Assert.Equal(None, formats.[3].language)
    Assert.Equal(Some "Dubbed", formats.[3].audio_track_name)

[<Fact>]
let ``parseFormats detects original audio and skips dubs`` () =
    let json = JsonNode.Parse """{ "formats": [
        { "format_id": "orig", "vcodec": "none", "acodec": "mp4a", "audio_track": { "id": "original", "name": "Original audio" } },
        { "format_id": "dub", "vcodec": "none", "acodec": "mp4a", "audio_track": { "id": "dub", "name": "Dubbed" } },
        { "format_id": "plain", "vcodec": "none", "acodec": "mp4a" }
    ] }"""
    let formats = parseFormats json
    Assert.Equal(Some true, formats.[0].audio_is_original)
    Assert.Equal(None, formats.[1].audio_is_original)
    Assert.Equal(None, formats.[2].audio_is_original)

[<Fact>]
let ``pickBestCombo prefers avc1 mp4 within size limit`` () =
    let formats =
        formatsOf
            [ videoOnly "137" "mp4" "avc1.640028"   // 1080p avc1 mp4
              videoOnly "303" "webm" "vp9"          // 1080p vp9 webm
              audioOnly "140" "m4a" "mp4a" ]
    match pickBestCombo 60.0 (parseFormats formats) with
    | Some (v, a, _) ->
        Assert.Equal("137", v.format_id)
        Assert.Equal("140", a.format_id)
    | None -> Assert.Fail "Expected a combo to be selected"

[<Fact>]
let ``pickBestCombo returns none without both stream kinds`` () =
    let formats = parseFormats (formatsOf [ videoOnly "137" "mp4" "avc1.640028" ])
    Assert.True(pickBestCombo 60.0 formats |> Option.isNone)
