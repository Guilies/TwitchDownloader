# GIPHY chat rendering

GIFs with media metadata can be rendered in standalone chat video and in both
combined video/chat paths. Ordinary VOD downloads cannot yet be relied on to
provide that metadata. This is partial support, not a claim that downloading a
VOD recovers its native GIF messages.

## Twitch API limitation (checked September 25, 2026)

Twitch's public web client carries live GIF fragments as `gif: { id, url }` and
normalizes them to `content: { __typename: "GifContent", gifID, url }`, retaining
the fragment text as an accessible label. See the
[live fragment adapter](https://assets.twitch.tv/assets/53884-6f3a64e86846950059b9.js).

The client's `VideoCommentsByOffsetOrCursor` query still selects only `text` and
`emote { id emoteID from }` on VOD message fragments. See the
[VOD chat query bundle](https://assets.twitch.tv/assets/90643-828ab328a0fd1acf9296.js).
Read-only requests against `https://gql.twitch.tv/gql` rejected `content`, `gif`
and `gifContent` on `VideoCommentMessageFragment`; `gif` was also rejected on
`VideoCommentMessage`. These are schema validation results, not a captured VOD
containing a GIF. No such sample was available during implementation.

The downloader's persisted query is intentionally unchanged: adding rejected
fields would break all chat downloads. Its conversion code preserves optional
`gif` metadata if a source supplies it, including fragments without text. The
current persisted query does not supply that field. A GIF label alone is
insufficient to identify the original image; this implementation does not guess
an image from that label. End-to-end VOD acquisition needs a replay source that
returns GIF IDs/URLs, or a separate recording of the live chat metadata.

## Archive format

Chat JSON version 1.5.0 adds optional `gif` metadata to a message fragment:

```json
{
  "text": "The original GIF description",
  "gif": {
    "id": "the-giphy-id",
    "url": "https://media.giphy.com/media/the-giphy-id/giphy.gif"
  }
}
```

Existing fragments and older chat files remain supported. A source/importer
with the native live payload can preserve its `gif` object in this shape.
This change does not add a live chat recorder or a separate import UI.

`embeddedData.gifs` holds original GIF/WebP bytes using the existing
`EmbedEmoteData` structure. Its `id` is the uppercase SHA-256 hex digest of the
normalized media URL (path plus query, excluding fragment), and `imageScale` is
1. Cache files use that digest under the `gifs` cache directory. Download/update
embedding includes these images. Rendering supports embedded data, disk cache,
and online fetching without requiring third-party emote options. Offline mode
does not fetch missing GIFs.

Only HTTPS GIPHY media image URLs are accepted. Missing or undecodable images
fall back to the message label. An unlabeled unavailable image displays
`[GIF unavailable]`. GIFs reserve rows below preceding text and scale to the
chat width and height; the default upper bounds are 240 by 160 pixels at the
reference font size, without upscaling. Animations use the existing VOD-clock
emote compositor, including its mask path. Animated GIFs prevent the combined
pipeline from reducing chat frame rate as though all images were static.
HTML export supports embedded GIFs and remote image URLs as well.

## Validation

Tests use an authored two-frame GIF, with unequal 200 ms and 400 ms frame
durations. The source-payload fixture is synthetic, not a Twitch VOD capture.
Coverage includes archive round-trip/cloning, optional metadata conversion,
offline cache and corrupt-embed fallback, cancellation, deduplication, narrow
layouts, extreme aspect ratios, following text, and HTML export.

The FFmpeg integration tests render and encode both combined paths, then decode
pixels from the resulting MP4s to verify GIF animation after a fractional chat
trim. They also verify output dimensions, frame rate, duration, audio, and the
video pane. FFmpeg/ffprobe must be on PATH (or set `FFMPEG_PATH`); the existing
integration harness returns early when these tools are unavailable.

```powershell
dotnet test TwitchDownloaderCore.Tests/TwitchDownloaderCore.Tests.csproj --filter "FullyQualifiedName~GifMessageTests|FullyQualifiedName~GifAnimationSurvivesBothCombinedEncodingPaths"
```

To finish native VOD support, capture a real replay response containing a GIF,
verify how its ID/URL is exposed, then update the query and conversion fixture
together. Rendering and combined encoding are already covered independently.
