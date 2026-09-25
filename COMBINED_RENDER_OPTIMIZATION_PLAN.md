# Combined Render Pipeline Optimization Plan

## Document Status

- **Purpose:** Implementation plan for improving combined VOD + chat render performance, correctness, resource usage, and maintainability.
- **Scope:** `CombinedRenderer`, VOD acquisition, chat acquisition and rendering, FFmpeg composition, supporting WPF/CLI integration, and performance-test infrastructure.
- **Relationship to existing documentation:** This document complements `DESIGN_DOCUMENT_COMBINED_RENDER.md` and supersedes performance priorities in the chat-only `TwitchDownloaderCore/ChatRender/OPTIMIZATION_PLAN.md` where they conflict.
- **Created from:** A source review and local Release build/test inspection performed on August 6, 2026.

## 1. Executive Summary

The combined renderer works, but the critical path currently performs substantially more work than necessary. Its largest cost is architectural: chat is rendered and encoded into a temporary H.264 video, then immediately decoded, combined with the VOD, and encoded into H.264 again.

The target design should stream raw chat frames directly into the final FFmpeg composition process. This removes one complete video encode/decode generation, the temporary chat video, and most intermediate chat I/O.

```text
Current pipeline

Metadata
   |
   +-> VOD download -> remux -------------------------+
   |                                                  |
   +-> chat download -> embedded JSON -> parse        +-> decode/scale/hstack
                         -> render -> H.264 chat MP4 --+   -> final H.264 encode


Target pipeline

Shared resolved metadata and exact trim interval
   |
   +-> VOD acquisition -------------------------------+
   |                                                  +-> one FFmpeg scale/pad/
   +-> chat model -> assets -> raw BGRA frame stream -+   hstack/encode operation
```

Correctness problems must be fixed before measuring the final improvement. In particular, the refactored chat renderer currently calculates its update interval incorrectly, so the default configuration updates approximately every five seconds rather than every 0.2 seconds. The current implementation is therefore doing less rendering work than intended and is not a valid performance baseline.

## 2. Current Pipeline Assessment

### 2.1 Positive characteristics

- Chat rendering has been split into reasonably focused components.
- Image categories are fetched concurrently in `ImageFetcher`.
- VOD segment downloads already support parallelism.
- The combined workflow has progress, cancellation tokens, and temporary-file cleanup concepts in place.
- Rendering and composition are sufficiently separated to make an incremental migration possible.

### 2.2 Principal limitations

| Priority | Finding | Consequence |
|---|---|---|
| P0 | Chat is encoded to a temporary MP4 and decoded for final composition | An entire lossy encode/decode generation plus significant CPU and disk I/O |
| P0 | `SKBitmap.Bytes` creates a managed copy for every raw frame write | Very high allocation and large-object-heap pressure |
| P0 | Chat update cadence is incorrectly calculated | Incorrect output behavior and a misleadingly fast baseline |
| P0 | The canvas cache strongly retains transient bitmaps and canvases | Memory growth and possible degradation or failure during long renders |
| P1 | Comment position is repeatedly found with a backward linear scan | Work can grow toward billions of predicate evaluations on long chats |
| P1 | VOD and chat acquisition run serially | Critical-path time is the sum of independent operations |
| P1 | Combined mode serializes embedded chat assets and reparses them | Avoidable network data expansion, memory consumption, CPU, and disk I/O |
| P1 | All Unicode emoji and many unused provider assets are loaded or scanned | High fixed startup cost and poor scaling with chat/emote counts |
| P1 | Scaling and frame-rate filters perform redundant work | Frames may be duplicated at full resolution before scaling |
| P2 | FFmpeg execution, option scoping, cancellation, and UI settings are inconsistent | Tuning is unreliable and failures are difficult to diagnose |

## 3. Detailed Findings

### 3.1 Double encoding in combined mode

`CombinedRenderer` executes VOD download, chat download, chat rendering, and final composition as sequential phases. The chat-render phase produces a temporary H.264 file using `libx264`, which is then supplied as an input to another FFmpeg process and decoded for the final `libx264` encode.

Relevant code:

- [`CombinedRenderer.cs`](TwitchDownloaderCore/CombinedRenderer.cs), orchestration around lines 118-211
- [`CombinedRenderer.cs`](TwitchDownloaderCore/CombinedRenderer.cs), temporary chat arguments around lines 443-470
- [`CombinedRenderer.cs`](TwitchDownloaderCore/CombinedRenderer.cs), final composition around lines 532-660

The optimized fast path should have exactly one video encode. Chat frames should be supplied to the final FFmpeg process as raw BGRA frames through a pipe. The VOD remains a normal file or concat-manifest input.

### 3.2 Per-frame managed copies

`SectionRenderer` writes `frame.Bytes` to the encoder stream. SkiaSharp's `Bytes` property returns a copy. At the default combined 1080p layout, the chat panel is approximately 480 x 1080 BGRA at 30 frames per second:

```text
480 * 1080 * 4 * 30 = 62,208,000 bytes/second
                      = approximately 208 GiB/hour
```

At a 4K-derived 960 x 2160 chat panel, this approaches 834 GiB/hour of managed copy traffic. Each frame buffer is also large enough to enter the large object heap.

Relevant code: [`SectionRenderer.cs`](TwitchDownloaderCore/ChatRender/SectionRenderer.cs), especially lines 153-173.

Use `SKBitmap.GetPixelSpan()` with the synchronous `Stream.Write(ReadOnlySpan<byte>)` API after verifying BGRA format, row stride, and bitmap lifetime. The frame must remain alive until the synchronous write completes.

### 3.3 Incorrect update cadence

The legacy/options calculation correctly derives update frames as:

```csharp
Math.Max(1, (int)(UpdateRate * Framerate))
```

The refactored renderer instead divides frame rate by update rate. At 30 fps with a 0.2-second update rate, this produces 150 frames between updates, or five seconds, rather than six frames, or 0.2 seconds. This is a 25x timing error.

Relevant code:

- [`ChatRenderOptions.cs`](TwitchDownloaderCore/Options/ChatRenderOptions.cs), around line 34
- [`SectionRenderer.cs`](TwitchDownloaderCore/ChatRender/SectionRenderer.cs), around lines 77-78

This must be corrected before collecting a baseline.

### 3.4 Algorithmic work in frame generation

`GenerateUpdateFrame` allocates a full-size bitmap before confirming that the visible state has changed. It also calls `FindLastIndex` across the comment list at every update. For a four-hour render with 100,000 comments and five updates per second, a uniform-distribution estimate is approximately 3.6 billion predicate checks.

Relevant code: [`SectionRenderer.cs`](TwitchDownloaderCore/ChatRender/SectionRenderer.cs), around lines 263-338.

Required changes:

1. Ensure comments have a stable chronological order after processing and dispersion.
2. Perform one binary search at the trim start.
3. Advance a monotonic comment cursor as render time increases.
4. Determine whether visible state changes before allocating a replacement bitmap.
5. Track frames generated, visual updates, and duplicate writes as separate metrics.

The desired complexity is approximately `O(comment count + update count)`.

### 3.5 Unbounded native resource retention

`BitmapCache` stores an `SKCanvas` for each `SKBitmap` in a dictionary. Transient update, visible-comment, and line bitmaps are disposed elsewhere without first being removed from the cache. The dictionary therefore keeps strong references to both managed wrappers and native Skia resources for the renderer's lifetime.

Relevant code:

- [`BitmapCache.cs`](TwitchDownloaderCore/ChatRender/Caching/BitmapCache.cs), around lines 16-24
- [`SectionRenderer.cs`](TwitchDownloaderCore/ChatRender/SectionRenderer.cs), transient disposal around lines 273, 338, and 497

Use scoped canvases for transient bitmaps. Limit caches to deliberately reusable, immutable resources; bound them by entry count or estimated bytes; and give the render session explicit ownership and disposal.

### 3.6 Redundant chat serialization and embedded assets

Combined mode downloads chat, embeds emote and badge data, serializes the resulting object graph into a temporary JSON file, parses it back into `ChatRoot`, recreates/scales images, and ultimately clears embedded data. The temporary JSON is then deleted.

Relevant code:

- [`CombinedRenderer.cs`](TwitchDownloaderCore/CombinedRenderer.cs), chat options and parse around lines 412-492
- [`ChatDownloader.cs`](TwitchDownloaderCore/ChatDownloader.cs), embedding and serialization around lines 316-343
- [`ChatRenderer.cs`](TwitchDownloaderCore/ChatRenderer.cs), parse and cleanup around lines 147-175
- [`ImageFetcher.cs`](TwitchDownloaderCore/ChatRender/Processing/ImageFetcher.cs), image preparation around line 45

Short-term, set `EmbedData` to false in combined mode and avoid commenter backfilling when avatars are disabled. Long-term, make `ChatDownloader` return a `ChatRoot` plus resolved asset references. JSON output should be an optional external sink, not an internal transport format.

### 3.7 Asset discovery and loading hotspots

The renderer currently:

- Loads and scales the entire bundled Noto emoji collection for every render.
- Matches each grapheme by searching the emoji catalog using PLINQ and a concurrent collection.
- Builds a regex for each third-party emote and scans comments to determine whether it is used.
- Can fetch unused badge versions and other assets.

Relevant code:

- [`ImageFetcher.cs`](TwitchDownloaderCore/ChatRender/Processing/ImageFetcher.cs), around lines 47-56 and 171-190
- [`MessageRenderer.cs`](TwitchDownloaderCore/ChatRender/Message/MessageRenderer.cs), around lines 279-318
- [`TwitchHelper.cs`](TwitchDownloaderCore/TwitchHelper.cs), around lines 530-534

Pre-tokenize messages once, build sets of referenced emotes/badges/emoji, and load only those assets. Use a Unicode sequence trie or longest-match dictionary for emoji. Intersect provider metadata with the referenced-token set, and apply bounded provider-aware download concurrency.

### 3.8 Repeated metadata work and serial acquisition

`CombinedRenderer`, `VideoDownloader`, and `ChatDownloader` independently retrieve overlapping VOD metadata, chapters, access tokens, and playlist information. Combined mode also forces chat download threads to one even though `ChatDownloader` can partition requests.

Relevant code:

- [`CombinedRenderer.cs`](TwitchDownloaderCore/CombinedRenderer.cs), around lines 259-323 and 426
- [`VideoDownloader.cs`](TwitchDownloaderCore/VideoDownloader.cs), around lines 71 and 668
- [`ChatDownloader.cs`](TwitchDownloaderCore/ChatDownloader.cs), around lines 379-401 and 476 onward

Introduce a shared `ResolvedVodPlan` and run the independent VOD and chat acquisition legs concurrently. Chat parsing and asset preparation should overlap VOD downloading where practical.

### 3.9 Scaling, frame rate, and trim correctness

Combined scaling currently uses output dimensions as a placeholder for source dimensions despite the selected M3U8 quality carrying its own resolution and frame rate. This can distort nonstandard aspect ratios, perform unnecessary upscaling, or produce a final width smaller than requested when height-limited scaling is used without horizontal padding.

Quality selection is also resolved differently in combined mode and `VideoDownloader`; combined mode uses a case-sensitive exact match and can fall back to 60 fps while the downloader selects a 30 fps stream. Decimal frame rates are rounded to integers.

The filter graph changes frame rate before Lanczos scaling, includes a no-op chat scale, and also supplies output `-r`/`-vsync` controls. A local synthetic filter-only comparison for 20 seconds of 1080p30 to 60 fps showed the current ordering at approximately 5.625 CPU seconds and 97,980 KiB peak RSS versus 3.250 CPU seconds and 59,352 KiB when scaling first and removing the redundant chat scale. This is indicative only; it excludes encoding, networking, storage, and Skia rendering.

Safe-trim behavior can select full HLS segments while chat uses the exact requested trim, creating a synchronization offset. Fractional chat trim timestamps are also floored to whole seconds before frame conversion.

Required behavior:

- Resolve quality once using the same matching rules for every component.
- Preserve rational frame rates.
- Use the selected source dimensions.
- Scale with original aspect ratio and pad the VOD cell on both axes.
- Establish one canonical exact output interval and timestamp origin.
- Apply frame-rate normalization once, after spatial scaling/composition, only when required.
- Explicitly map filtered video and optional VOD audio.
- Specify output duration and timestamp-reset behavior.

### 3.10 FFmpeg execution and configuration

The final encoder is hardcoded to software `libx264`, preset `medium`, CRF 23. The `-threads` option is placed before the first input and is therefore scoped as an input/decoder option rather than reliably tuning the output encoder. Visible WPF controls for some thread/hardware settings are hardcoded or have no-op handlers.

Cancellation waits on FFmpeg but does not consistently terminate it. The chat encoder closes stdin and calls a timed `WaitForExit` without reliably handling timeout or exit status. Diagnostic stderr handling mixes asynchronous reading with `ReadToEnd` patterns.

Relevant code:

- [`CombinedRenderer.cs`](TwitchDownloaderCore/CombinedRenderer.cs), around lines 564-660 and 740-749
- [`SectionRenderer.cs`](TwitchDownloaderCore/ChatRender/SectionRenderer.cs), around lines 256-260
- [`PageCombinedRender.xaml.cs`](TwitchDownloaderWPF/Pages/PageCombinedRender.xaml.cs), around lines 298-334 and 520-529
- [`CombinedRender.cs`](TwitchDownloaderCLI/Modes/CombinedRender.cs), around lines 26-27

Create a shared FFmpeg runner that:

- Uses structured argument construction.
- Continuously drains stderr.
- Uses `-progress pipe:1 -nostats` or a similarly machine-readable progress channel.
- Closes input, then kills the entire process tree if cancellation does not complete promptly.
- Awaits process exit and stream-drain completion.
- Validates exit code and expected output.
- Reports phase and frame progress to WPF and CLI consistently.

Expose typed speed/fidelity profiles. Add capability-probed hardware encoders with a safe software fallback only after a correctness and output-quality comparison.

### 3.11 Text measurement cache correctness

The global text measurement cache key omits typeface and other style identity. Its collision-check behavior also cannot reliably distinguish all first collisions. This can produce incorrect wrapping across fonts and permits global growth.

Relevant code:

- [`TextMeasurementCache.cs`](TwitchDownloaderCore/ChatRender/Caching/TextMeasurementCache.cs)
- [`TextUtilities.cs`](TwitchDownloaderCore/ChatRender/Utilities/TextUtilities.cs)

Make this cache render-session scoped and bounded. Key it by complete text or a collision-safe representation plus all measurement-affecting typeface/style properties.

## 4. Target Architecture

### 4.1 Shared plan

Add a `ResolvedVodPlan`-style model containing:

- VOD metadata and chapters
- Selected quality and stream URL
- Exact source dimensions
- Rational source frame rate
- Requested trim interval
- Realized segment interval, if segment acquisition requires expansion
- Canonical output interval and timestamp origin

All acquisition and rendering components should consume this shared plan rather than independently resolving metadata.

### 4.2 Chat frame boundary

Separate chat layout/frame generation from FFmpeg by introducing an interface conceptually similar to:

```csharp
public interface IChatFrameSource : IAsyncDisposable
{
    ChatFrameFormat Format { get; }
    ValueTask<bool> RenderNextAsync(
        IFrameSink destination,
        CancellationToken cancellationToken);
}
```

The exact API can change during design, but it should provide:

- Explicit pixel format, dimensions, frame rate, and timestamp semantics
- No FFmpeg dependency in layout code
- A zero-copy or single-copy synchronous frame-write path
- Testability using an in-memory sink
- Explicit ownership of all Skia resources

### 4.3 Final composition

The final FFmpeg process should receive:

- **Input 0:** Downloaded/remuxed VOD, initially as a file
- **Input 1:** Raw BGRA chat frames through stdin or a dedicated pipe
- **Filter graph:** VOD scale/pad, chat placement, and `hstack`
- **Maps:** Filtered video and `0:a?`
- **Output:** One final encode

A later optimization may consume the downloader's verified concat manifest directly and eliminate the intermediate VOD remux, but this should be treated as a separate change with format and seekability tests.

## 5. Implementation Roadmap

### Phase 0: Correctness and measurement foundation

**Goal:** Produce a trustworthy baseline and prevent performance changes from hiding output regressions.

- [ ] Repair test discovery and move test projects to a supported, compatible target/runner combination.
- [ ] Add deterministic local VOD and `ChatRoot` fixtures that do not require Twitch network access.
- [ ] Fix update interval calculation.
- [ ] Resolve selected dimensions and rational frame rate once.
- [ ] Define exact trim and PTS behavior across video, audio, and chat.
- [ ] Add explicit output mapping, duration, and timestamp assertions.
- [ ] Instrument phase time, CPU, RSS, allocations, GC/LOH, temporary I/O, and FFmpeg progress.
- [ ] Record the corrected baseline for the benchmark matrix in Section 6.

**Exit criteria:** Correctness tests pass, tests are discovered in CI/local runs, and every benchmark produces a repeatable phase/metric report.

#### Phase 0 implementation status — August 6, 2026

Completed in the initial Phase 0 slice:

- [x] Moved test executables to .NET 8 and aligned xUnit 2.9.3 with the 2.8.2 Visual Studio adapter.
- [x] Restored test discovery: 409 core and 21 CLI tests now execute successfully after detailed-progress coverage.
- [x] Added deterministic tests for cadence, fractional ticks, quality selection, source geometry, rational frame rate, padding, timeline bounds, filter construction, and FFmpeg stream mapping.
- [x] Corrected chat update cadence and fractional trim-to-frame conversion.
- [x] Resolved the selected playlist quality once for combined-render geometry and frame rate.
- [x] Preserved reported decimal frame rates and canonicalized common NTSC rates as FFmpeg rational expressions.
- [x] Replaced placeholder source dimensions with the selected playlist dimensions.
- [x] Added horizontal and vertical padding so the combined frame has exact requested dimensions.
- [x] Established one validated exact trim interval for both the VOD and chat download legs; trimmed combined renders reject safe segment overhang.
- [x] Scaled before frame-rate normalization, removed the redundant chat scale/output `-r`/`-vsync`, and explicitly mapped filtered video plus optional VOD audio.
- [x] Added combined total and per-phase wall-time, CPU-time, managed-allocation, GC, current-RSS, RSS-delta, and peak-RSS log records.
- [x] Added generated, network-free VOD/chat media plus a deterministic fractional-timeline `ChatRoot` fixture.
- [x] Added FFprobe-backed assertions for output duration, zero-based timestamps, dimensions, rational frame rate, and audio/video stream presence.
- [x] Added FFmpeg nonzero-exit, missing-output, and process-tree cancellation semantics with integration coverage.
- [x] Added temporary-byte deltas, raw frame bytes, frame/update/visual-change counters, cache counts, and image-asset counts.
- [x] Added OS/runtime/architecture/processor/GC and FFmpeg-version environment records.
- [x] Recorded a three-run corrected synthetic two-pass baseline under `benchmarks/combined-render`.

Extended baseline work still required before production rollout:

- [ ] Run the longer 720p, 1080p soak, and 4K benchmark matrix on representative hardware.
- [ ] Add OS-level I/O counters if per-path byte deltas prove insufficient during profiling.

#### Initial corrected baseline

The network-free benchmark measures the existing two-pass architecture after the Phase 0 correctness fixes. Generated source creation is excluded. The workload is five seconds of 640x360 VOD plus 160x360 offline chat, composed to 800x360 at 30 fps with three deterministic comments.

| Metric | Run 1 | Run 2 | Run 3 | Median |
|---|---:|---:|---:|---:|
| Chat render | 111 ms | 105 ms | 105 ms | 105 ms |
| Final composition | 217 ms | 206 ms | 201 ms | 206 ms |
| Measured pipeline total | 328 ms | 311 ms | 306 ms | 311 ms |
| Process CPU | 78 ms | 109 ms | 156 ms | 109 ms |
| Managed allocation | 37,577,192 B | 37,581,240 B | 37,562,712 B | 37,577,192 B |

Every run produced 151 chat frames, 25 update attempts, three visual updates, 34,790,400 raw chat bytes, a 19,820-byte temporary chat video, and a 525,492-byte final output. The canvas cache retained 32 entries at the end of the short render. These values directly support the Phase 1 priorities: remove per-frame managed copies, avoid work on unchanged frames, and repair transient canvas ownership.

The machine-readable record and reproduction instructions are in `benchmarks/combined-render/phase0-baseline-2026-08-06.json` and `benchmarks/combined-render/README.md`.

### Phase 1: Chat renderer hot-path repairs

**Goal:** Make chat-frame generation allocation-safe and approximately linear in input size.

- [x] Replace `FindLastIndex` with a stable sorted list and monotonic cursor.
- [x] Avoid bitmap allocation when visual state has not changed.
- [x] Replace `SKBitmap.Bytes` writes with span-based writes.
- [x] Remove transient bitmaps/canvases from global or session-long caches.
- [x] Bound reusable bitmap, canvas, and text caches.
- [x] Remove forced full garbage collections.
- [x] Load only referenced emoji, badge versions, and emotes.
- [x] Replace full-catalog emoji matching and per-emote regex scans with indexes.
- [x] Add a reduced chat frame-rate path when there are no animated assets.

**Exit criteria:** No per-frame LOH array allocation, cursor work is `O(N + U)`, and cache/native memory plateaus during a one-hour soak.

#### Phase 1 implementation status — August 6, 2026

Completed across the first two Phase 1 slices:

- [x] Replaced `SKBitmap.Bytes` frame writes with synchronous `GetPixelSpan()` writes, eliminating the per-frame managed pixel-buffer copy.
- [x] Added a single binary search at trim start followed by a monotonic comment cursor.
- [x] Restored stable chronological order after comment dispersion/filtering.
- [x] Avoided allocating an update bitmap when the newest visible comment has not changed.
- [x] Ensured a fractional trim always receives an initial update frame instead of regenerating an untracked frame on every pre-boundary tick.
- [x] Added explicit transient canvas release, final visible-comment cleanup, cached-bitmap ownership, and animated-frame-buffer disposal.
- [x] Removed forced full garbage collections from normal and exceptional chat-render paths.
- [x] Replaced per-grapheme PLINQ catalog scans with an immutable Unicode-sequence index.
- [x] Pre-scanned chat and decoded/extracted only referenced Unicode emoji assets.
- [x] Replaced provider-emote regex/comment rescans with one whitespace-token index shared by all providers.
- [x] Replaced the hash-only text measurement cache with an exact, paint-aware, 4,096-entry LRU and exposed its occupancy in render metrics.
- [x] Bounded timestamp bitmaps to a 256-entry LRU; eviction releases both the native canvas and bitmap.
- [x] Added cache-growth tests covering 10,000 unique text keys and 3,600 timestamp keys.
- [x] Filtered embedded and downloaded first-party emotes to referenced IDs and Twitch badges to referenced name/version pairs before decoding and scaling.
- [x] Added a combined-only static-chat path that preserves the exact update grid at the lowest compatible integer frame rate; animated emotes and cheermotes retain the requested frame rate.

Still required in Phase 1:

- [ ] Run the one-hour memory/cache soak and the longer representative benchmark fixtures.

The first checkpoint reduced median managed allocation from 37,577,192 bytes to 2,726,496 bytes (92.7%) and live end-of-render canvas entries from 32 to 4 (87.5%) on the same fixture. The remaining four canvases correspond to the current update and three visible comments and are explicitly released during cleanup. The short fixture's median time moved from 311 ms to 327 ms, which is within process-startup noise for a five-second synthetic render and is not treated as a speed conclusion. Details are recorded in `benchmarks/combined-render/phase1-checkpoint-2026-08-06.json`.

The static-chat checkpoint reduced emitted frames and raw pipe bytes from 151 / 34,790,400 to 26 / 5,990,400 (82.8%) on the same fixture. The temporary chat file fell from 19,820 to 16,992 bytes. This short test remains dominated by FFmpeg startup, so its wall-clock values are recorded for reproducibility rather than used as a throughput conclusion. Details are in `benchmarks/combined-render/phase1-static-checkpoint-2026-08-06.json`.

### Phase 2: Acquisition DAG and in-memory handoff

**Goal:** Overlap independent work and remove intermediate chat serialization.

- [x] Implement the shared resolved VOD plan.
- [x] Start VOD and chat acquisition concurrently.
- [x] Overlap chat processing/assets with VOD segment acquisition.
- [x] Return `ChatRoot` directly from the combined download path.
- [x] Make JSON serialization an optional output sink.
- [x] Disable embedded assets and irrelevant commenter backfill in combined mode.
- [x] Add configurable, bounded chat request concurrency.

**Exit criteria:** Combined mode does not create a temporary chat JSON file on its default fast path, and acquisition time approaches the slower independent leg rather than their sum.

#### Phase 2 implementation status — August 7, 2026

The combined acquisition path now follows `metadata/plan -> [VOD download || (chat download -> asset fetch/chat render)] -> composite`. A shared resolved plan supplies the already-fetched video metadata, chapters, selected master-playlist stream, and quality paths to both downloaders, removing duplicate metadata, chapter, token, and master-playlist requests.

`ChatDownloader.DownloadChatRootAsync` returns the downloaded model directly and `ChatRenderer.SetChatRoot` consumes it without a serialization/deserialization round trip. Combined mode skips embedded asset packing and commenter backfill unless the caller explicitly sets `ChatOutputFile`; when set, the same in-memory model is serialized once as an optional JSON output before rendering. `ChatDownloadThreads` is configurable and validated from 1 through 10.

The acquisition coordinator starts both branches before awaiting either. A failure or cancellation in one branch cancels and awaits its sibling, with regression coverage for concurrency and the prior wait-before-cancel deadlock. The deterministic in-memory checkpoint retained the corrected 26-frame/5,990,400-byte static render while reducing median managed allocation from the Phase 1 static checkpoint's 2,883,496 bytes to 2,702,864 bytes (6.3%). Short-fixture wall time remains startup-sensitive and is not used as the concurrency conclusion; a network-backed representative run is still needed to quantify acquisition overlap. Details are in `benchmarks/combined-render/phase2-in-memory-checkpoint-2026-08-07.json`.

### Phase 3: Single-pass combined composition

**Goal:** Remove the temporary chat video and perform exactly one final encode.

- [x] Extract an FFmpeg-independent chat frame source/sink boundary.
- [x] Feed raw chat frames into the final FFmpeg process.
- [x] Scale and pad the VOD cell using actual source geometry.
- [x] Map filtered video and optional source audio explicitly.
- [x] Remove temporary chat MP4 creation and deletion.
- [x] Validate pipe backpressure, encoder failure propagation, and cancellation.
- [x] Compare output visual quality to the corrected two-pass baseline.

**Exit criteria:** The fast path contains one video encode, no temporary chat video, synchronized audio/video/chat, and clean cancellation with no orphan process.

#### Phase 3 implementation status — August 7, 2026

Chat rendering now has an FFmpeg-independent raw stream boundary. `ChatRenderer.PrepareAsync` performs asset and layout preparation while the VOD downloads, and `RenderRawFramesAsync` emits frames only when the final compositor is ready. `SectionRenderer.RenderSectionToStreams` writes synchronously into the consumer stream, so the OS pipe provides bounded backpressure without a second frame queue or per-frame buffer copy.

The final FFmpeg process opens the downloaded VOD as input 0 and raw BGRA/RGBA chat on `pipe:0` as input 1. The existing corrected scale/pad/rational-FPS filter maps `[outv]` and optional `0:a?`, then performs the pipeline's only video encode. The temporary H.264 chat file and its encode/decode cycle have been removed.

The streamed FFmpeg runner continuously drains logs, validates exit/output, closes stdin at end-of-stream, propagates producer errors, and kills the entire process tree on cancellation. Integration tests cover successful composition, producer failure, and prompt cancellation. The generated result retains 800x360 geometry, 30 fps, zero-based video timestamps, approximately five-second duration, and source audio. Its SSIM against the corrected two-pass reference is 0.993553, above the 0.98 regression floor.

On the three-run short fixture, Phase 3 reduced median measured render/composite time from 280 ms to 241 ms (13.9%), managed allocation from 2,702,864 to 2,472,832 bytes (8.5%), and intermediate chat videos from one to zero. The fixture is still too short for a production throughput claim, but the eliminated encode is structural rather than timing-dependent. Details are in `benchmarks/combined-render/phase3-single-pass-checkpoint-2026-08-07.json`.

### Phase 4: FFmpeg and profile tuning

**Goal:** Tune remaining encoder/filter costs without sacrificing predictable quality.

- [x] Remove redundant scale/frame-rate operations.
- [x] Scale before frame-rate expansion when expansion is required.
- [x] Add typed software speed/fidelity profiles.
- [x] Probe and validate supported hardware encoders.
- [x] Correct thread-option scoping.
- [x] Wire WPF controls and CLI arguments to the same validated options.
- [x] Add a robust shared FFmpeg process runner and progress parser.
- [x] Consider a direct concat-manifest VOD input after format testing.
- [x] Consider a bounded frame producer/consumer only if profiling still shows Skia layout as the bottleneck.

**Exit criteria:** Profiles have documented speed/quality tradeoffs, unsupported hardware configurations fall back safely, and output settings behave identically through WPF and CLI.

#### Phase 4 implementation status — August 7, 2026

The single-pass graph retains exactly one VOD scale and performs that scale/pad before rational frame-rate expansion. Raw chat enters at its reduced effective rate and is expanded only in the final filter graph. The final output no longer adds a redundant output `-r` or `-vsync` operation.

Software encoding now uses a typed profile rather than free-form FFmpeg fragments:

| Profile | libx264 policy | Intended tradeoff | Median 720p encode | SSIM vs lossless source |
|---|---|---|---:|---:|
| Fast | `veryfast`, CRF 24 | Lowest latency and smaller files; modest quality reduction | 213 ms | 0.975183 |
| Balanced (default) | `medium`, CRF 23 | General-purpose speed/quality default | 349 ms | 0.976760 |
| Quality | `slow`, CRF 20 | Higher fidelity and larger output at substantially higher CPU cost | 615 ms | 0.978002 |

These figures isolate software encoding on a deterministic five-second 1280x720 source. Fast was 39.0% faster than Balanced; Quality was 76.2% slower than Balanced. They document direction and regression behavior, not a production throughput prediction or a claim that CRF values across hardware encoders are equivalent.

The same profile enum also maps to NVIDIA NVENC, Intel Quick Sync, AMD AMF, and Apple VideoToolbox settings. An explicit hardware choice, or `AutoHardware`, must complete a one-frame 256x256 smoke encode using the exact selected profile arguments before the main render starts. The probe deliberately avoids smaller frames that supported NVENC drivers may reject solely because they fall below the hardware's minimum dimensions. Auto probes VideoToolbox on macOS; on Windows/Linux it tries NVENC, Quick Sync, then AMF, choosing the first functional result. If none succeeds, combined rendering logs a warning and safely falls back to the corresponding libx264 software profile. Windows release workflows also reject an FFmpeg bundle that does not advertise `h264_nvenc`. Hardware-specific performance and quality still require representative tests on each supported device family.

`FfmpegThreads` now controls filter-complex threads for all encoders but is emitted as codec `-threads` only for libx264; hardware encoders retain their driver-managed threading. WPF and CLI both populate the same validated `CombinedRenderOptions` fields for VOD download threads, chat download threads, FFmpeg threads, render profile, and encoder. Both default to Balanced software encoding. WPF exposes typed selectors, while CLI exposes `--render-profile`, `--encoder`, and `--chat-threads`.

The shared runner now consumes FFmpeg `-progress pipe:2` records, parses microsecond or timestamp output monotonically, maps final encoding into the combined task's 75–100% range, continuously drains logs, validates output, propagates producer failures, and kills the process tree on cancellation.

Combined-render progress is now represented as structured phase snapshots rather than a single shared percentage. The overall value uses fixed component weights—5% metadata/encoder validation, 40% VOD acquisition, 15% chat acquisition, 15% chat preparation, and 25% final composition—and never decreases. A separate current-action value resets for metadata, concurrent acquisition, and composition. During acquisition it combines all three concurrent branches while retaining their individual percentages for the detail display. VOD's internal download, verification, and finalization resets are mapped into one continuous branch value. During composition, complete FFmpeg progress records provide media time, encoder speed, frame count, and ETA. WPF exposes distinct Overall and Action bars; CLI emits the same snapshot as a live status line. Older `ITaskProgress` consumers retain the original single-progress fallback.

Two optional architectural changes were evaluated and deliberately deferred:

- Direct concat-manifest VOD input needs fixtures for MPEG-TS and fMP4 segments, discontinuities, Twitch muted/replacement segments, source-audio variations, and exact fractional trims. The verified remuxed VOD remains the compatibility boundary until that matrix exists.
- A second producer/consumer queue is not warranted by current profiling: chat preparation is approximately 23 ms while final encoding is approximately 208 ms on the canonical short fixture, and the OS stdin pipe already supplies bounded backpressure. Another queue would retain large native frames and complicate Skia ownership without addressing the measured bottleneck.

The three-run Balanced canonical checkpoint retained one encode, no temporary chat video, 0.993553 SSIM against the corrected two-pass reference, and a median 231 ms measured total. The detailed canonical and profile records are in `benchmarks/combined-render/phase4-profile-checkpoint-2026-08-07.json`.

## 6. Benchmark and Validation Plan

### 6.1 Fixture matrix

| Fixture | Purpose |
|---|---|
| 10-minute 720p, low-density static chat | Fast regression and CI benchmark |
| 60-minute 1080p, typical chat with static and animated emotes | Representative user workload |
| 4-hour 1080p, high-density chat | Memory, cache, and algorithmic soak |
| Short 4K render | Raw bandwidth, allocation, scaling, and encoder stress |
| Fractionally trimmed 29.97/59.94 fps inputs | Timestamp and frame-rate correctness |

Use generated or checked-in deterministic media where licensing and repository size permit. Network download time should be measured separately from local rendering so Twitch/network variability cannot hide a renderer regression.

### 6.2 Metrics

- End-to-end wall time
- Acquisition, parsing, asset preparation, chat layout, and FFmpeg phase time
- Real-time factor and FFmpeg-reported speed
- Process CPU time
- Peak application and FFmpeg RSS
- Managed bytes allocated and Gen 0/1/2 collection counts
- Large-object-heap allocations
- Native Skia/cache object counts and estimated bytes
- Temporary bytes read and written
- Frames generated, visually changed, written, duplicated, and dropped
- Output size, SSIM/VMAF where encoder profiles change, and a representative visual inspection

Run at least three warmed iterations and compare medians. Record machine, OS, .NET runtime, FFmpeg build, storage type, and encoder/GPU details with results.

### 6.3 Correctness gates

- Output dimensions exactly match the requested layout.
- VOD, chat, and audio alignment remain within one output frame.
- Message order and update timing are correct.
- 29.97 and 59.94 fps remain rational rather than silently becoming 30 and 60.
- Fractional trims select the correct first chat and video frames.
- Memory and cache counts plateau during a long render.
- Cancellation leaves no FFmpeg process or locked temporary file.
- Failure and nonzero FFmpeg exit codes reach the caller with usable diagnostics.
- The optimized fast path performs one final video encode and creates neither temporary chat MP4 nor chat JSON.

### 6.4 Initial performance gates

These are rollout targets, not predictions:

- At least 20% lower median end-to-end time against the corrected baseline.
- No benchmark scenario more than 5% slower without an explained quality improvement.
- Greater than 95% reduction in managed allocations associated with frame writes.
- Bounded memory in the four-hour soak.
- No measurable synchronization or visual-quality regression.

Avoid using “less than twice VOD download time” as the primary metric. Network and encoder speeds are independent and highly variable. Prefer local real-time factor plus per-phase resource metrics.

## 7. Test Work Required

The Release solution build succeeds. As of Phase 4, 409 core tests and 21 CLI tests are discovered and pass. The remaining build warnings are the .NET 6 target lifetime, the existing Mono.Posix prerelease/readme package warning, and the unused WPF `_scalingInfo` field.

Completed acceptance coverage:

- [x] Align the test target framework and xUnit/Visual Studio runner packages.
- [x] Confirm nonzero discovered-test counts in local output.
- [x] Add unit tests for update cadence at 30/60 fps and 0.2/1/5-second rates.
- [x] Add tests for the monotonic event cursor, including equal timestamps and dispersion.
- [x] Add tests for selected quality, dimensions, rational frame rate, and fallback behavior.
- [x] Add fractional trim and PTS synchronization tests.
- [x] Add filter-graph snapshot/argument tests.
- [x] Add FFmpeg cancellation, nonzero exit, stderr-drain, streamed-producer failure, and progress-parser tests.
- [x] Add resource-lifetime and cache-bound tests where practical.
- [x] Add integration tests for both the corrected two-pass reference and single-pass pipeline.

Still required before broad production rollout:

- [ ] Run the representative one-hour memory/cache soak and long 1080p/4K performance matrix.
- [ ] Add CI execution for the FFmpeg-backed integration and benchmark correctness gates.
- [ ] Run hardware profile quality/performance tests on each supported device family.
- [ ] Add the segment-format compatibility matrix before enabling direct concat-manifest VOD input.

## 8. Change Sequencing and Risk Controls

1. Do not combine the update-cadence correction with performance claims; establish the corrected baseline first.
2. Land low-risk allocation, cursor, and lifetime repairs before the architectural pipe change.
3. Keep the existing two-pass composition available behind an internal fallback during initial single-pass rollout.
4. Make the new path opt-in during early benchmark and compatibility testing, then change the default only after gates pass.
5. Compare encoder profiles at approximately matched visual quality, not solely matched CRF numbers across different encoders.
6. Keep network acquisition benchmarks separate from deterministic local rendering benchmarks.
7. Treat Skia objects as non-thread-safe unless the specific ownership pattern is proven safe; do not parallelize frame drawing merely because CPU remains available.

## 9. Suggested Work Breakdown

| Work item | Depends on | Risk | Expected value |
|---|---|---:|---:|
| Repair test discovery and add baseline telemetry | None | Low | Foundational |
| Fix cadence, quality, rational FPS, trim, and scaling | Tests/telemetry | Medium | Correctness-critical |
| Span-based frame writes | Correct baseline | Low | High allocation reduction |
| Monotonic cursor and delayed bitmap allocation | Correct baseline | Medium | High CPU/allocation reduction |
| Fix canvas/cache ownership | Correct baseline | Medium | High stability improvement |
| Used-asset indexing | Chat fixtures | Medium | Medium-to-high startup/render improvement |
| Shared resolved VOD plan and acquisition concurrency | Correctness model | Medium | Medium wall-time improvement |
| In-memory chat handoff | Shared plan | Medium | Medium I/O/memory improvement |
| Single-pass raw-frame composition | Frame source + FFmpeg runner | High | Highest render-time improvement |
| Encoder/hardware profiles | Single-pass correctness | Medium | Workload/hardware dependent |
| Direct segment-manifest composition | Single-pass maturity | High | Additional I/O/time reduction |

## 10. Definition of Done

The optimization program is complete when:

- The combined renderer uses one final encode by default.
- No temporary chat MP4 or JSON is required by the normal combined path.
- VOD/chat acquisition and preparation overlap where independent.
- Source geometry, rational frame rate, trim, PTS, and audio mapping are consistent across all components.
- Chat rendering performs no per-frame large managed copy.
- Comment traversal is monotonic and caches remain bounded.
- FFmpeg failures and cancellation are deterministic and leave no child process behind.
- WPF and CLI expose the same effective render options.
- Automated tests are discovered and cover timing, synchronization, filter arguments, and process failure behavior.
- The benchmark matrix passes the correctness and initial performance gates in Section 6.
