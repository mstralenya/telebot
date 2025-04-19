module Telebot.TwitterData

// Define the data structures
type TwitterSize = {
    height: int
    width: int
}

type TwitterMediaExtended = {
    altText: string option
    size: TwitterSize
    thumbnail_url: string
    mediaType: string
    url: string
}

type TwitterQrt = {
    allSameType: bool
    article: string option
    combinedMediaUrl: string option
    communityNote: string option
    conversationID: string
    date: string
    date_epoch: int64
    hasMedia: bool
    hashtags: string list
    lang: string
    likes: int
    mediaURLs: string list
    media_extended: TwitterMediaExtended list
    pollData: string option
    possibly_sensitive: bool
    qrtURL: string
    replies: int
    retweets: int
    text: string option
    tweetID: string
    tweetURL: string
    user_name: string
    user_profile_image_url: string
    user_screen_name: string
}

type Tweet = {
    date_epoch: int64
    hashtags: string list
    likes: int
    mediaURLs: string list
    media_extended: TwitterMediaExtended list
    pollData: string option
    possibly_sensitive: bool
    qrt: TwitterQrt option
    qrtURL: string
    replies: int
    retweets: int
    text: string option
    tweetID: string
    tweetURL: string
    user_name: string
    user_profile_image_url: string
    user_screen_name: string
}