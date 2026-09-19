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
