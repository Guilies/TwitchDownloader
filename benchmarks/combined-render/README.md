# Combined Render Baselines

The benchmark suite is deterministic and network-free. The canonical benchmark exercises the current single-pass combined pipeline while also producing a corrected two-pass reference for visual comparison:

1. Generate a local VOD fixture with FFmpeg.
2. Prepare a local fractional-timeline `ChatRoot` and stream its raw frames into the final compositor.
3. Scale/pad the VOD, expand static chat at the final filter stage, compose both inputs, and perform one H.264 encode.
4. Render a corrected two-pass reference and compare SSIM.
5. Validate duration, dimensions, frame rate, timestamps, video, and audio with FFprobe.

The generated VOD is not included in the measured interval. This keeps acquisition/network variability separate from renderer performance.

Run the benchmark with:

```powershell
dotnet test TwitchDownloaderCore.Tests\TwitchDownloaderCore.Tests.csproj `
  --configuration Release `
  --filter "Category=Benchmark" `
  --logger "console;verbosity=detailed"
```

Run it three times after a warm-up and compare medians. The test prints a JSON record containing phase times, process CPU time, managed allocation, temporary/output sizes, and chat-frame/cache counters.

Recorded checkpoints are kept as dated JSON files in this directory. Phase 0 and the first Phase 1 checkpoint used the requested 30 fps chat stream; `phase1-static-checkpoint-2026-08-06.json` records the static-asset path.
`phase2-in-memory-checkpoint-2026-08-07.json` records direct `ChatRoot` handoff without a temporary JSON file.
`phase3-single-pass-checkpoint-2026-08-07.json` records raw chat piping into the only video encode and its SSIM comparison against the corrected two-pass reference.
`phase4-profile-checkpoint-2026-08-07.json` records the single-pass Balanced checkpoint plus three-run Fast/Balanced/Quality software encode speed, size, and SSIM results.

Run the isolated software-profile comparison with:

```powershell
dotnet test TwitchDownloaderCore.Tests\TwitchDownloaderCore.Tests.csproj `
  --configuration Release `
  --filter "Category=ProfileBenchmark" `
  --logger "console;verbosity=detailed"
```

Set `FFMPEG_PATH` to an explicit FFmpeg executable when it is not on `PATH`. The matching `ffprobe` executable must be in the same directory.

The short fixture is intended for rapid regression detection. The longer 720p, 1080p soak, and 4K scenarios in the optimization plan remain required before a production rollout.
