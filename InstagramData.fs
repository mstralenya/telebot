module Telebot.InstagramData

open System.Text.Json.Serialization

[<Struct>]
type Dimension = { Height: int; Width: int }

type SharingFrictionInfo = {
    [<JsonPropertyName("should_have_sharing_friction")>]
    ShouldHaveSharingFriction: bool
    [<JsonPropertyName("bloks_app_url")>]
    BloksAppUrl: string option
}

type DisplayResource = {
    [<JsonPropertyName("src")>]
    SourceUrl: string
    [<JsonPropertyName("config_width")>]
    ConfigWidth: int
    [<JsonPropertyName("config_height")>]
    ConfigHeight: int
}

type TaggedUserEdge = {
    [<JsonPropertyName("edges")>]
    Edges: obj list
}

type MediaNode = {
    [<JsonPropertyName("__typename")>]
    TypeName: string
    [<JsonPropertyName("id")>]
    Id: string
    [<JsonPropertyName("shortcode")>]
    Shortcode: string
    [<JsonPropertyName("dimensions")>]
    Dimensions: Dimension
    [<JsonPropertyName("gating_info")>]
    GatingInfo: obj option
    [<JsonPropertyName("fact_check_overall_rating")>]
    FactCheckOverallRating: obj option
    [<JsonPropertyName("fact_check_information")>]
    FactCheckInformation: obj option
    [<JsonPropertyName("sensitivity_friction_info")>]
    SensitivityFrictionInfo: obj option
    [<JsonPropertyName("sharing_friction_info")>]
    SharingFrictionInfo: SharingFrictionInfo
    [<JsonPropertyName("media_overlay_info")>]
    MediaOverlayInfo: obj option
    [<JsonPropertyName("media_preview")>]
    MediaPreview: string option
    [<JsonPropertyName("display_url")>]
    DisplayUrl: string
    [<JsonPropertyName("video_url")>]
    VideoUrl: string
    [<JsonPropertyName("display_resources")>]
    DisplayResources: DisplayResource list
    [<JsonPropertyName("accessibility_caption")>]
    AccessibilityCaption: string option
    [<JsonPropertyName("is_video")>]
    IsVideo: bool
    [<JsonPropertyName("tracking_token")>]
    TrackingToken: string
    [<JsonPropertyName("upcoming_event")>]
    UpcomingEvent: obj option
    [<JsonPropertyName("edge_media_to_tagged_user")>]
    EdgeMediaToTaggedUser: TaggedUserEdge
}

type Edge = {
    [<JsonPropertyName("node")>]
    Node: MediaNode
}

type EdgeSidecarToChildren = {
    [<JsonPropertyName("edges")>]
    Edges: Edge list
}

[<CLIMutable>]
type CaptionNode = {
    [<JsonPropertyName("created_at")>]
    CreatedAt: string
    [<JsonPropertyName("text")>]
    Text: string
    [<JsonPropertyName("id")>]
    Id: string
}

[<CLIMutable>]
type CaptionEdge = {
    [<JsonPropertyName("node")>]
    Node: CaptionNode
}

type InstagramXdt = {
    [<JsonPropertyName("video_url")>]
    VideoUrl: string option
    [<JsonPropertyName("display_url")>]
    ImageUrl: string option
    [<JsonPropertyName("is_video")>]
    IsVideo: bool
    [<JsonPropertyName("edge_media_to_caption")>]
    EdgeMediaToCaption: {| Edges: CaptionEdge list |}
    [<JsonPropertyName("edge_sidecar_to_children")>]
    EdgeSidecarToChildren: EdgeSidecarToChildren option
}

type Data = {
    [<JsonPropertyName("xdt_shortcode_media")>]
    InstagramXdt: InstagramXdt option
}

type InstagramMediaResponse = {
    [<JsonPropertyName("data")>]
    Data: Data option
}