module Telebot.PrometheusMetrics

open Prometheus


// Global metric definitions
let newMessageCounter = Metrics.CreateCounter("telebot_new_messages_total", "Number of new messages")
let processingTimeSummary = Metrics.CreateSummary("telebot_processing_time_milliseconds", "Summary of message processing times", 
                                                   SummaryConfiguration(Objectives = [
                                                                                      QuantileEpsilonPair(0.5, 0.01); 
                                                                                      QuantileEpsilonPair(0.9, 0.01); 
                                                                                      QuantileEpsilonPair(0.99, 0.01)]))

let messageSuccessCounter = Metrics.CreateCounter("telebot_success_total", "Number of successfully processed messages")
let messageFailureCounter = Metrics.CreateCounter("telebot_failures_total", "Number of failed message processing attempts")

let videoSizeFailureCounter =
    Metrics.CreateCounter("telebot_thumbnail_failures_total", "Number of failed thumbnail extractions")
let videoSizeSuccessCounter =
    Metrics.CreateCounter("telebot_thumbnail_successes_total", "Number of successful thumbnail extractions")
let thumbnailSuccessCounter =
    Metrics.CreateCounter("telebot_thumbnail_successes_total", "Number of successful thumbnail extractions")
let thumbnailFailureCounter =
    Metrics.CreateCounter("telebot_thumbnail_failures_total", "Number of failed thumbnail extractions")

let downloadCounter = 
    Metrics.CreateCounter("telebot_downloads_total", "Number of downloads")
    
let deleteCounter = 
    Metrics.CreateCounter("telebot_deletes_total", "Number of deletes")
    
let instagramSuccessCounter =
    Metrics.CreateCounter("telebot_instagram_successes_total", "Number of successful Instagram link extractions")
let instagramFailureCounter =
    Metrics.CreateCounter("telebot_instagram_failures_total", "Number of failed Instagram link extractions")
let instagramMissingVideoIdCounter =
    Metrics.CreateCounter("telebot_instagram_missing_video_id", "Number of missing video IDs")

let tiktokMissingVideoIdMetric =
    Metrics.CreateCounter("telebot_tiktok_missing_video_id", "Number of missing video IDs")
let tiktokSuccessMetric =
    Metrics.CreateCounter("telebot_tiktok_success", "Number of successful TikTok video downloads")