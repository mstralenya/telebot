# iOS rich-message workaround

Rich Twitter replies are opt-in through `TWITTER_RICH_MESSAGES_ENABLED=true`.
The default is disabled. The switch is also checked when delivering queued
rich replies; when disabled, their regular message/media fallback is used.

The renderer emits no media for an empty list, a standalone `<img>` or `<video>`
block for one valid item, and `<tg-collage>` only for two or more valid items.
The count is checked after invalid media URLs are removed. Tweet and quoted
tweet media stay separate, and original/translation edits use the same renderer.

This avoids a source-level Telegram iOS crash path: a one-item collage whose
image exceeds a 2:1 aspect ratio enters the mosaic algorithm and constructs an
invalid Swift range. Regression coverage includes the observed 1770x883 image
shape, single videos, mixed media, empty lists, and filtered sources.

This is a bot-side workaround, not an iOS client fix. It does not alter rich
messages already posted. No iPhone or simulator reproduction has been run.

Compose passes the flag from the stack's `.env`, defaulting to false. Set
`TWITTER_RICH_MESSAGES_ENABLED=true` there to enable it, or set it to false and
recreate the bot container to return to regular messages and media groups.
