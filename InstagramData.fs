module Telebot.InstagramData

open System.Text.Json.Serialization

[<Struct>]
type InstagramDimension = { Height: int; Width: int }

type InstagramSharingFrictionInfo = {
    [<JsonPropertyName("should_have_sharing_friction")>]
    ShouldHaveSharingFriction: bool
    [<JsonPropertyName("bloks_app_url")>]
    BloksAppUrl: string option
}

type InstagramDisplayResource = {
    [<JsonPropertyName("src")>]
    SourceUrl: string
    [<JsonPropertyName("config_width")>]
    ConfigWidth: int
    [<JsonPropertyName("config_height")>]
    ConfigHeight: int
}

type InstagramTaggedUserEdge = {
    [<JsonPropertyName("edges")>]
    Edges: obj list
}

type InstagramMediaNode = {
    [<JsonPropertyName("__typename")>]
    TypeName: string
    [<JsonPropertyName("id")>]
    Id: string
    [<JsonPropertyName("shortcode")>]
    Shortcode: string
    [<JsonPropertyName("dimensions")>]
    Dimensions: InstagramDimension
    [<JsonPropertyName("gating_info")>]
    GatingInfo: obj option
    [<JsonPropertyName("fact_check_overall_rating")>]
    FactCheckOverallRating: obj option
    [<JsonPropertyName("fact_check_information")>]
    FactCheckInformation: obj option
    [<JsonPropertyName("sensitivity_friction_info")>]
    SensitivityFrictionInfo: obj option
    [<JsonPropertyName("sharing_friction_info")>]
    SharingFrictionInfo: InstagramSharingFrictionInfo
    [<JsonPropertyName("media_overlay_info")>]
    MediaOverlayInfo: obj option
    [<JsonPropertyName("media_preview")>]
    MediaPreview: string option
    [<JsonPropertyName("display_url")>]
    DisplayUrl: string
    [<JsonPropertyName("video_url")>]
    VideoUrl: string
    [<JsonPropertyName("display_resources")>]
    DisplayResources: InstagramDisplayResource list
    [<JsonPropertyName("accessibility_caption")>]
    AccessibilityCaption: string option
    [<JsonPropertyName("is_video")>]
    IsVideo: bool
    [<JsonPropertyName("tracking_token")>]
    TrackingToken: string
    [<JsonPropertyName("upcoming_event")>]
    UpcomingEvent: obj option
    [<JsonPropertyName("edge_media_to_tagged_user")>]
    EdgeMediaToTaggedUser: InstagramTaggedUserEdge
}

type InstagramEdge = {
    [<JsonPropertyName("node")>]
    Node: InstagramMediaNode
}

type InstagramEdgeSidecarToChildren = {
    [<JsonPropertyName("edges")>]
    Edges: InstagramEdge list
}

[<CLIMutable>]
type InstagramCaptionNode = {
    [<JsonPropertyName("created_at")>]
    CreatedAt: string
    [<JsonPropertyName("text")>]
    Text: string
    [<JsonPropertyName("id")>]
    Id: string
}

[<CLIMutable>]
type InstagramCaptionEdge = {
    [<JsonPropertyName("node")>]
    Node: InstagramCaptionNode
}

type InstagramXdt = {
    [<JsonPropertyName("video_url")>]
    VideoUrl: string option
    [<JsonPropertyName("display_url")>]
    ImageUrl: string option
    [<JsonPropertyName("is_video")>]
    IsVideo: bool
    [<JsonPropertyName("edge_media_to_caption")>]
    EdgeMediaToCaption: {| Edges: InstagramCaptionEdge list |}
    [<JsonPropertyName("edge_sidecar_to_children")>]
    EdgeSidecarToChildren: InstagramEdgeSidecarToChildren option
}

type InstagramData = {
    [<JsonPropertyName("xdt_shortcode_media")>]
    InstagramXdt: InstagramXdt option
}

type InstagramMediaResponse = {
    [<JsonPropertyName("data")>]
    Data: InstagramData option
}