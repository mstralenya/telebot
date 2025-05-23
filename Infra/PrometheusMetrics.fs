module Telebot.PrometheusMetrics

open Prometheus

// Message processing metrics
let newMessageCounter =
    Metrics.CreateCounter("telebot_new_messages_total", "Number of new messages")

let processingTimeSummary =
    Metrics.CreateSummary(
        "telebot_processing_time_milliseconds",
        "Summary of message processing times",
        SummaryConfiguration(
            Objectives =
                [
                    QuantileEpsilonPair(0.5, 0.01)
                    QuantileEpsilonPair(0.9, 0.01)
                    QuantileEpsilonPair(0.99, 0.01)
                ]
        )
    )

let messageSuccessCounter =
    Metrics.CreateCounter("telebot_success_total", "Number of successfully processed messages")

let messageFailureCounter =
    Metrics.CreateCounter("telebot_failures_total", "Number of failed message processing attempts")

// HTTP client metrics
let httpRequestsTotal =
    Metrics.CreateCounter("telebot_http_requests_total", "Total number of HTTP requests", [|"method"; "host"; "status_code"|])

let httpRequestDuration =
    Metrics.CreateHistogram("telebot_http_request_duration_seconds", "HTTP request duration", [|"method"; "host"|])

let httpRequestSize =
    Metrics.CreateHistogram("telebot_http_request_size_bytes", "HTTP request size in bytes", [|"method"; "host"|])

let httpResponseSize =
    Metrics.CreateHistogram("telebot_http_response_size_bytes", "HTTP response size in bytes", [|"method"; "host"|])

// Resource management metrics
let activeConnectionsGauge =
    Metrics.CreateGauge("telebot_active_connections", "Number of active HTTP connections")

let memoryUsageGauge =
    Metrics.CreateGauge("telebot_memory_usage_bytes", "Current memory usage in bytes")

let gcCollectionsTotal =
    Metrics.CreateCounter("telebot_gc_collections_total", "Total number of garbage collections", [|"generation"|])

// File operations metrics
let downloadCounter =
    Metrics.CreateCounter("telebot_downloads_total", "Number of downloads")

let deleteCounter =
    Metrics.CreateCounter("telebot_deletes_total", "Number of deletes")

let fileSizeHistogram =
    Metrics.CreateHistogram("telebot_file_size_bytes", "File size distribution", [|"file_type"|])

let diskSpaceGauge =
    Metrics.CreateGauge("telebot_disk_space_bytes", "Available disk space in bytes")

// Video processing metrics
let videoSizeFailureCounter =
    Metrics.CreateCounter("telebot_video_size_failures_total", "Number of failed video size extractions")

let videoSizeSuccessCounter =
    Metrics.CreateCounter("telebot_video_size_successes_total", "Number of successful video size extractions")

let thumbnailSuccessCounter =
    Metrics.CreateCounter("telebot_thumbnail_successes_total", "Number of successful thumbnail extractions")

let thumbnailFailureCounter =
    Metrics.CreateCounter("telebot_thumbnail_failures_total", "Number of failed thumbnail extractions")

let videoProcessingDuration =
    Metrics.CreateHistogram("telebot_video_processing_duration_seconds", "Video processing duration", [|"operation"|])

// Platform-specific metrics
let instagramSuccessCounter =
    Metrics.CreateCounter("telebot_instagram_successes_total", "Number of successful Instagram link extractions")

let instagramFailureCounter =
    Metrics.CreateCounter("telebot_instagram_failures_total", "Number of failed Instagram link extractions")

let instagramMissingVideoIdCounter =
    Metrics.CreateCounter("telebot_instagram_missing_video_id_total", "Number of missing Instagram video IDs")

let tiktokMissingVideoIdMetric =
    Metrics.CreateCounter("telebot_tiktok_missing_video_id_total", "Number of missing TikTok video IDs")

let tiktokSuccessMetric =
    Metrics.CreateCounter("telebot_tiktok_success_total", "Number of successful TikTok video downloads")

let tiktokFailureMetric =
    Metrics.CreateCounter("telebot_tiktok_failure_total", "Number of failed TikTok video downloads")

let youtubeSuccessCounter =
    Metrics.CreateCounter("telebot_youtube_successes_total", "Number of successful YouTube video downloads")

let youtubeFailureCounter =
    Metrics.CreateCounter("telebot_youtube_failures_total", "Number of failed YouTube video downloads")

let twitterSuccessCounter =
    Metrics.CreateCounter("telebot_twitter_successes_total", "Number of successful Twitter media downloads")

let twitterFailureCounter =
    Metrics.CreateCounter("telebot_twitter_failures_total", "Number of failed Twitter media downloads")

// Message bus metrics
let messageBusQueueSize =
    Metrics.CreateGauge("telebot_message_bus_queue_size", "Current message bus queue size")

let messageBusProcessingRate =
    Metrics.CreateCounter("telebot_message_bus_processed_total", "Total messages processed by the bus", [|"handler"|])

let messageBusErrors =
    Metrics.CreateCounter("telebot_message_bus_errors_total", "Total message bus errors", [|"error_type"|])

// Application health metrics
let applicationStartTime =
    Metrics.CreateGauge("telebot_application_start_time_seconds", "Application start time in seconds since epoch")

let applicationUptime =
    Metrics.CreateGauge("telebot_application_uptime_seconds", "Application uptime in seconds")

let healthCheckStatus =
    Metrics.CreateGauge("telebot_health_check_status", "Health check status (1 = healthy, 0 = unhealthy)", [|"check_name"|])

// Rate limiting metrics
let rateLimitedRequestsCounter =
    Metrics.CreateCounter("telebot_rate_limited_requests_total", "Number of rate-limited requests", [|"platform"|])

let retryAttemptsCounter =
    Metrics.CreateCounter("telebot_retry_attempts_total", "Number of retry attempts", [|"operation"; "attempt"|])

// Initialize application metrics
let initializeApplicationMetrics () =
    let startTime = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() |> float
    applicationStartTime.Set(startTime)

// Update uptime metric (should be called periodically)
let updateUptimeMetric () =
    let currentTime = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() |> float
    let startTime = applicationStartTime.Value
    applicationUptime.Set(currentTime - startTime)

// Memory metrics collection
let updateMemoryMetrics () =
    let memoryUsage = System.GC.GetTotalMemory(false) |> float
    memoryUsageGauge.Set(memoryUsage)
    
    // GC collection counts
    for generation in 0..2 do
        let collections = System.GC.CollectionCount(generation) |> float
        gcCollectionsTotal.WithLabels([|generation.ToString()|]).IncTo(collections)

// Disk space metrics (basic implementation)
let updateDiskSpaceMetrics () =
    try
        let currentDir = System.IO.Directory.GetCurrentDirectory()
        let drive = System.IO.DriveInfo(currentDir)
        diskSpaceGauge.Set(drive.AvailableFreeSpace |> float)
    with
    | _ -> () // Ignore errors in metrics collection
