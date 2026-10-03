module Telebot.Tests.InstagramTests

open Xunit
open Telebot.Instagram.Instagram
open Telebot.Replies

[<Fact>]
let ``story regex accepts shared story links with tracking parameters`` () =
    let url = "https://www.instagram.com/stories/dylan_rourke/3988409343112617832?utm_source=ig_story_item_share&stkn=MXdyZ21pdGNjYmpoeg=="
    let links = getLinks storyRegex (Some $"look {url}")
    Assert.Equal<string list>([ url ], links)

[<Theory>]
[<InlineData("https://instagram.com/stories/user.name/3988409343112617832/")>]
[<InlineData("https://www.instagram.com/stories/user_name/3988409343112617832?igsh=test")>]
let ``story regex accepts supported host and username forms`` (url: string) =
    Assert.True(storyRegex.IsMatch url)

[<Theory>]
[<InlineData("https://www.instagram.com/stories/highlights/123456789/")>]
[<InlineData("https://www.instagram.com/stories/user/not-a-number/")>]
let ``story regex rejects highlights and invalid media ids`` (url: string) =
    Assert.False(storyRegex.IsMatch url)

[<Fact>]
let ``story media id converts to Instagram shortcode without precision loss`` () =
    Assert.Equal(Some "DdZrSqDE59o", mediaIdToShortcode "3988409343112617832")

[<Fact>]
let ``post shortcode converts to numeric media id without precision loss`` () =
    Assert.Equal(Some "3989006527293304671", shortcodeToMediaId "DdbzE1KCC9f")

[<Fact>]
let ``post regex preserves carousel media index`` () =
    let url = "https://www.instagram.com/p/DdbzE1KCC9f/?img_index=18&stkn=MTQyN3owdjZzaWlqbA=="
    Assert.Equal<string list>([ url ], getLinks postRegex (Some url))
    Assert.Equal(Some 18, tryGetMediaIndex url)

[<Theory>]
[<InlineData("https://www.instagram.com/p/DdbzE1KCC9f/")>]
[<InlineData("https://www.instagram.com/p/DdbzE1KCC9f/?img_index=0")>]
[<InlineData("https://www.instagram.com/p/DdbzE1KCC9f/?img_index=bad")>]
let ``invalid or missing carousel media index is ignored`` (url: string) =
    Assert.Equal(None, tryGetMediaIndex url)

[<Fact>]
let ``Open Graph media parser prefers video metadata over its thumbnail`` () =
    let html = """<meta property="og:image" content="https://cdn.example/thumb.jpg"><meta property="og:video" content="https://cdn.example/video.mp4?a=1&amp;b=2">"""
    Assert.Equal(Some("https://cdn.example/video.mp4?a=1&b=2", true), tryGetOpenGraphMedia html)

[<Fact>]
let ``Open Graph media parser classifies image-only slides as photos`` () =
    let html = """<meta property="og:image" content="https://cdn.example/photo.jpg">"""
    Assert.Equal(Some("https://cdn.example/photo.jpg", false), tryGetOpenGraphMedia html)

[<Theory>]
[<InlineData("/videos/abc/1", "https://eeinstagram.com/videos/abc/1")>]
[<InlineData("https://cdn.example/video.mp4", "https://cdn.example/video.mp4")>]
let ``proxy media URLs resolve relative to their source`` (mediaUrl: string) (expected: string) =
    Assert.Equal(expected, resolveMediaUrl "https://eeinstagram.com" mediaUrl)

[<Theory>]
[<InlineData("video/mp4", true)>]
[<InlineData("VIDEO/WEBM", true)>]
[<InlineData("image/jpeg", false)>]
let ``proxy content type identifies direct media`` (contentType: string) (isVideo: bool) =
    Assert.Equal(Some isVideo, classifyMediaContentType (Some contentType))

[<Theory>]
[<InlineData("text/html")>]
[<InlineData("application/octet-stream")>]
let ``proxy content type leaves non-media responses for HTML parsing`` (contentType: string) =
    Assert.Equal(None, classifyMediaContentType (Some contentType))

[<Fact>]
let ``modern Instagram parser preserves carousel item media types`` () =
    let json = """{
      "data": { "xig_polaris_media": { "if_not_gated_logged_out": {
        "caption": { "text": "caption" },
        "carousel_media": [
          { "media_type": 1, "image_versions2": { "candidates": [{ "url": "https://cdn.example/one.jpg" }] } },
          { "media_type": 2, "video_versions": [{ "url": "https://cdn.example/two.mp4" }], "image_versions2": { "candidates": [{ "url": "https://cdn.example/two.jpg" }] } }
        ]
      } } }
    }"""
    Assert.Equal(
        Some([ ("https://cdn.example/one.jpg", false); ("https://cdn.example/two.mp4", true) ], Some "caption"),
        parseModernInstagramMedia json
    )

[<Theory>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData("<html>rate limited</html>")>]
let ``legacy Instagram parser rejects empty and non-JSON responses`` (body: string) =
    Assert.Equal(None, tryParseInstagramMediaResponse body)

[<Fact>]
let ``legacy Instagram parser accepts an empty data response`` () =
    let parsed = tryParseInstagramMediaResponse "{\"data\":null}"
    Assert.True(parsed.IsSome)
    Assert.Equal(None, parsed.Value.Data)
