using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Tests.Fixtures;
using TwitchDownloaderCore.Tools;
using Xunit.Abstractions;

namespace TwitchDownloaderCore.Tests.CombinedRenderTests
{
    public sealed partial class FfmpegIntegrationTests
    {
        private readonly ITestOutputHelper _output;

        public FfmpegIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        [Trait("Category", "Integration")]
        public async Task NonzeroExitIsReportedWithItsLogPath()
        {
            if (!TryResolveFfmpeg(out var ffmpegPath, out _))
                return;

            var directory = CreateTestDirectory();
            var logPath = Path.Combine(directory, "failure.log");
            try
            {
                var exception = await Assert.ThrowsAsync<FfmpegExecutionException>(() =>
                    FfmpegRunner.RunAsync(
                        ffmpegPath,
                        new[] { "-definitely-not-an-ffmpeg-option" },
                        directory,
                        logPath,
                        expectedOutputPath: null,
                        StubTaskProgress.Instance,
                        CancellationToken.None));

                Assert.NotEqual(0, exception.ExitCode);
                Assert.Equal(logPath, exception.LogPath);
                Assert.True(File.Exists(logPath));
                Assert.NotEmpty(await File.ReadAllTextAsync(logPath));
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        [Fact]
        [Trait("Category", "Integration")]
        public async Task SuccessfulExitWithoutExpectedOutputIsRejected()
        {
            if (!TryResolveFfmpeg(out var ffmpegPath, out _))
                return;

            var directory = CreateTestDirectory();
            var missingOutput = Path.Combine(directory, "missing.mp4");
            try
            {
                var exception = await Assert.ThrowsAsync<FfmpegExecutionException>(() =>
                    FfmpegRunner.RunAsync(
                        ffmpegPath,
                        new[] { "-version" },
                        directory,
                        Path.Combine(directory, "missing-output.log"),
                        missingOutput,
                        StubTaskProgress.Instance,
                        CancellationToken.None));

                Assert.Equal(0, exception.ExitCode);
                Assert.Contains("did not create", exception.Message);
                Assert.False(File.Exists(missingOutput));
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        [Fact]
        [Trait("Category", "Integration")]
        public async Task CancellationTerminatesTheFfmpegProcessTreePromptly()
        {
            if (!TryResolveFfmpeg(out var ffmpegPath, out _))
                return;

            var directory = CreateTestDirectory();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    FfmpegRunner.RunAsync(
                        ffmpegPath,
                        new[]
                        {
                            "-hide_banner", "-loglevel", "error",
                            "-re", "-f", "lavfi", "-i", "testsrc=size=64x64:rate=30",
                            "-t", "30", "-f", "null", "-"
                        },
                        directory,
                        Path.Combine(directory, "cancellation.log"),
                        expectedOutputPath: null,
                        StubTaskProgress.Instance,
                        cancellation.Token));

                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        [Fact]
        [Trait("Category", "Integration")]
        public async Task StreamedInputProducerFailureTerminatesFfmpegAndPropagates()
        {
            if (!TryResolveFfmpeg(out var ffmpegPath, out _))
                return;

            var directory = CreateTestDirectory();
            try
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    FfmpegRunner.RunWithInputAsync(
                        ffmpegPath,
                        new[]
                        {
                            "-f", "rawvideo", "-pix_fmt", "bgra", "-video_size", "2x2",
                            "-framerate", "1", "-i", "pipe:0", "-f", "null", "-"
                        },
                        directory,
                        Path.Combine(directory, "producer-failure.log"),
                        expectedOutputPath: null,
                        StubTaskProgress.Instance,
                        (_, _) => throw new InvalidOperationException("producer failed"),
                        CancellationToken.None));

                Assert.Equal("producer failed", exception.Message);
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        [Fact]
        [Trait("Category", "Integration")]
        public async Task StreamedInputCancellationTerminatesFfmpegPromptly()
        {
            if (!TryResolveFfmpeg(out var ffmpegPath, out _))
                return;

            var directory = CreateTestDirectory();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    FfmpegRunner.RunWithInputAsync(
                        ffmpegPath,
                        new[]
                        {
                            "-f", "rawvideo", "-pix_fmt", "bgra", "-video_size", "2x2",
                            "-framerate", "1", "-i", "pipe:0", "-f", "null", "-"
                        },
                        directory,
                        Path.Combine(directory, "stream-cancellation.log"),
                        expectedOutputPath: null,
                        StubTaskProgress.Instance,
                        (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
                        cancellation.Token));

                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        [Fact]
        [Trait("Category", "Integration")]
        public async Task StreamedInputReportsStructuredFfmpegProgress()
        {
            if (!TryResolveFfmpeg(out var ffmpegPath, out _))
                return;

            var directory = CreateTestDirectory();
            var snapshots = new List<FfmpegProgressSnapshot>();
            try
            {
                await FfmpegRunner.RunWithInputAsync(
                    ffmpegPath,
                    new[]
                    {
                        "-progress", "pipe:2", "-nostats",
                        "-f", "rawvideo", "-pix_fmt", "bgra", "-video_size", "2x2",
                        "-framerate", "10", "-i", "pipe:0",
                        "-frames:v", "5", "-f", "null", "-"
                    },
                    directory,
                    Path.Combine(directory, "structured-progress.log"),
                    expectedOutputPath: null,
                    StubTaskProgress.Instance,
                    async (stream, token) =>
                    {
                        await stream.WriteAsync(new byte[2 * 2 * 4 * 5], token);
                    },
                    CancellationToken.None,
                    expectedDuration: TimeSpan.FromSeconds(0.5),
                    detailedProgress: snapshots.Add);

                Assert.NotEmpty(snapshots);
                Assert.Equal(100, snapshots[^1].Percent);
                Assert.Equal(TimeSpan.FromSeconds(0.5), snapshots[^1].Duration);
                Assert.Equal(TimeSpan.Zero, snapshots[^1].Remaining);
                Assert.True(snapshots
                    .Select(x => x.Percent)
                    .Zip(snapshots.Skip(1).Select(x => x.Percent))
                    .All(pair => pair.First <= pair.Second));
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        [Fact]
        [Trait("Category", "Integration")]
        public async Task GeneratedFractionalFixtureHasExpectedTimelineGeometryAndStreams()
        {
            if (!TryResolveFfmpeg(out var ffmpegPath, out var ffprobePath))
                return;

            var directory = CreateTestDirectory();
            var vodPath = Path.Combine(directory, "vod.mp4");
            var chatPath = Path.Combine(directory, "chat.mp4");
            var outputPath = Path.Combine(directory, "combined.mp4");
            try
            {
                await RunFixtureFfmpeg(
                    ffmpegPath,
                    directory,
                    "vod.log",
                    vodPath,
                    new[]
                    {
                        "-y",
                        "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30000/1001",
                        "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
                        "-t", "1.5",
                        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                        "-c:a", "aac", "-shortest",
                        vodPath
                    });

                await RunFixtureFfmpeg(
                    ffmpegPath,
                    directory,
                    "chat.log",
                    chatPath,
                    new[]
                    {
                        "-y",
                        "-f", "lavfi", "-i", "color=c=0x111111:size=80x180:rate=30",
                        "-t", "1.5",
                        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                        chatPath
                    });

                var frameRate = new VideoFrameRate(30_000, 1_001);
                var scaling = CombinedRenderPlanner.CalculateScaling(
                    new Resolution(320, 180), 400, 180, 80, 180);
                var filter = CombinedRenderPlanner.BuildFilterComplex(scaling, frameRate);
                var arguments = CombinedRenderPlanner.BuildCompositeArguments(
                    vodPath, chatPath, filter, outputPath, 0);

                await RunFixtureFfmpeg(
                    ffmpegPath,
                    directory,
                    "combined.log",
                    outputPath,
                    arguments);

                using var probe = await ProbeAsync(ffprobePath, outputPath, directory);
                var root = probe.RootElement;
                var streams = root.GetProperty("streams").EnumerateArray().ToArray();
                var video = Assert.Single(streams, x => x.GetProperty("codec_type").GetString() == "video");

                Assert.Contains(streams, x => x.GetProperty("codec_type").GetString() == "audio");
                Assert.Equal(400, video.GetProperty("width").GetInt32());
                Assert.Equal(180, video.GetProperty("height").GetInt32());
                Assert.Equal("30000/1001", video.GetProperty("avg_frame_rate").GetString());
                Assert.InRange(ParseSeconds(video, "start_time"), 0, 0.001);

                var format = root.GetProperty("format");
                Assert.InRange(ParseSeconds(format, "start_time"), 0, 0.001);
                Assert.InRange(ParseSeconds(format, "duration"), 1.45, 1.60);
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        [Fact]
        [Trait("Category", "Integration")]
        public async Task GeneratedChatRootRendersOfflineFromItsFractionalTimeline()
        {
            if (!TryResolveFfmpeg(out var ffmpegPath, out var ffprobePath))
                return;

            var directory = CreateTestDirectory();
            var outputPath = Path.Combine(directory, "rendered-chat.mp4");
            try
            {
                var fixture = CombinedRenderFixtureFactory.CreateChatRoot(1.25, 2.75);

                var options = new ChatRenderOptions
                {
                    OutputFile = outputPath,
                    ChatWidth = 160,
                    ChatHeight = 180,
                    BackgroundColor = SKColor.Parse("#111111"),
                    AlternateBackgroundColor = SKColor.Parse("#191919"),
                    MessageColor = SKColors.White,
                    Font = "Inter Embedded",
                    FontSize = 12,
                    MessageFontStyle = SKFontStyle.Normal,
                    UsernameFontStyle = SKFontStyle.Bold,
                    Framerate = 10,
                    UpdateRate = 0.2,
                    ChatBadges = false,
                    BttvEmotes = false,
                    FfzEmotes = false,
                    StvEmotes = false,
                    Offline = true,
                    EmojiVendor = EmojiVendor.None,
                    SkipDriveWaiting = true,
                    FfmpegPath = ffmpegPath,
                    TempFolder = directory,
                    InputArgs = "-y -framerate {fps} -f rawvideo -pix_fmt {pix_fmt} -video_size {width}x{height} -i -",
                    OutputArgs = "-c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{save_path}\""
                };

                using var renderer = new ChatRenderer(options, StubTaskProgress.Instance);
                renderer.SetChatRoot(fixture);
                await renderer.RenderVideoAsync(CancellationToken.None);

                using var probe = await ProbeAsync(ffprobePath, outputPath, directory);
                var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                var video = Assert.Single(streams, x => x.GetProperty("codec_type").GetString() == "video");

                Assert.Equal(160, video.GetProperty("width").GetInt32());
                Assert.Equal(180, video.GetProperty("height").GetInt32());
                Assert.Equal("10/1", video.GetProperty("avg_frame_rate").GetString());
                Assert.InRange(ParseSeconds(video, "start_time"), 0, 0.001);
                Assert.InRange(
                    ParseSeconds(probe.RootElement.GetProperty("format"), "duration"),
                    1.55,
                    1.70);
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        [Fact]
        [Trait("Category", "Benchmark")]
        public async Task GeneratedSinglePassPipelineReportsCheckpoint()
        {
            if (!TryResolveFfmpeg(out var ffmpegPath, out var ffprobePath))
                return;

            const double durationSeconds = 5;
            var directory = CreateTestDirectory();
            var vodPath = Path.Combine(directory, "vod.mp4");
            var referenceChatPath = Path.Combine(directory, "reference-chat.mp4");
            var referenceOutputPath = Path.Combine(directory, "reference-combined.mp4");
            var outputPath = Path.Combine(directory, "combined.mp4");
            try
            {
                await RunFixtureFfmpeg(
                    ffmpegPath,
                    directory,
                    "vod.log",
                    vodPath,
                    new[]
                    {
                        "-y",
                        "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30",
                        "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
                        "-t", durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                        "-c:a", "aac", "-shortest",
                        vodPath
                    });

                var progress = new BenchmarkTaskProgress();
                var scaling = CombinedRenderPlanner.CalculateScaling(
                    new Resolution(640, 360), 800, 360, 160, 360);
                var filter = CombinedRenderPlanner.BuildFilterComplex(
                    scaling,
                    new VideoFrameRate(30, 1));

                var fixture = CombinedRenderFixtureFactory.CreateChatRoot(0.25, durationSeconds + 0.25);
                var chatOptions = CreateOfflineChatOptions(
                    ffmpegPath,
                    directory,
                    inputPath: string.Empty,
                    outputPath: string.Empty,
                    width: 160,
                    height: 360,
                    frameRate: 30);
                chatOptions.ReduceFramerateWhenStatic = true;

                var allocatedBefore = GC.GetTotalAllocatedBytes(false);
                using var process = Process.GetCurrentProcess();
                var cpuBefore = process.TotalProcessorTime;
                var totalStopwatch = Stopwatch.StartNew();
                var prepareStopwatch = Stopwatch.StartNew();

                using var renderer = new ChatRenderer(chatOptions, progress);
                renderer.SetChatRoot(fixture);
                await renderer.PrepareAsync(CancellationToken.None);
                prepareStopwatch.Stop();

                var arguments = CombinedRenderPlanner.BuildSinglePassCompositeArguments(
                    vodPath,
                    160,
                    360,
                    renderer.EffectiveFramerate,
                    SKImageInfo.PlatformColorType == SKColorType.Bgra8888 ? "bgra" : "rgba",
                    filter,
                    outputPath,
                    CombinedEncoderResolver.ResolveFromAvailable(
                        CombinedRenderEncoder.Software,
                        CombinedRenderSpeedProfile.Balanced,
                        new HashSet<string>()),
                    0);

                var encodeStopwatch = Stopwatch.StartNew();
                await FfmpegRunner.RunWithInputAsync(
                    ffmpegPath,
                    arguments,
                    directory,
                    Path.Combine(directory, "combined.log"),
                    outputPath,
                    progress,
                    (stream, token) => renderer.RenderRawFramesAsync(stream, outputPath, token),
                    CancellationToken.None);
                encodeStopwatch.Stop();
                totalStopwatch.Stop();
                long managedAllocatedBytes = GC.GetTotalAllocatedBytes(false) - allocatedBefore;
                double processCpuMilliseconds =
                    (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;

                using var probe = await ProbeAsync(ffprobePath, outputPath, directory);
                var format = probe.RootElement.GetProperty("format");
                var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                var video = Assert.Single(streams, x => x.GetProperty("codec_type").GetString() == "video");
                Assert.Equal(800, video.GetProperty("width").GetInt32());
                Assert.Equal(360, video.GetProperty("height").GetInt32());
                Assert.Equal("30/1", video.GetProperty("avg_frame_rate").GetString());
                _ = Assert.Single(streams, x => x.GetProperty("codec_type").GetString() == "audio");
                Assert.InRange(ParseSeconds(video, "start_time"), 0, 0.001);
                Assert.InRange(ParseSeconds(format, "duration"), 4.95, 5.15);

                var referenceOptions = CreateOfflineChatOptions(
                    ffmpegPath,
                    directory,
                    inputPath: string.Empty,
                    outputPath: referenceChatPath,
                    width: 160,
                    height: 360,
                    frameRate: 30);
                referenceOptions.ReduceFramerateWhenStatic = true;
                using (var referenceRenderer = new ChatRenderer(referenceOptions, StubTaskProgress.Instance))
                {
                    referenceRenderer.SetChatRoot(
                        CombinedRenderFixtureFactory.CreateChatRoot(0.25, durationSeconds + 0.25));
                    await referenceRenderer.RenderVideoAsync(CancellationToken.None);
                }
                await RunFixtureFfmpeg(
                    ffmpegPath,
                    directory,
                    "reference-combined.log",
                    referenceOutputPath,
                    CombinedRenderPlanner.BuildCompositeArguments(
                        vodPath,
                        referenceChatPath,
                        filter,
                        referenceOutputPath,
                        0));

                string ssimLogPath = Path.Combine(directory, "ssim.log");
                await FfmpegRunner.RunAsync(
                    ffmpegPath,
                    new[]
                    {
                        "-i", referenceOutputPath,
                        "-i", outputPath,
                        "-lavfi", "ssim",
                        "-f", "null", "-"
                    },
                    directory,
                    ssimLogPath,
                    expectedOutputPath: null,
                    StubTaskProgress.Instance,
                    CancellationToken.None);
                string ssimLog = await File.ReadAllTextAsync(ssimLogPath);
                var ssimMatch = Regex.Match(ssimLog, @"All:(?<score>\d+(?:\.\d+)?)");
                Assert.True(ssimMatch.Success, "FFmpeg did not report an aggregate SSIM score.");
                double ssim = double.Parse(
                    ssimMatch.Groups["score"].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(ssim >= 0.98, $"Single-pass SSIM {ssim:F6} fell below the corrected two-pass reference.");

                var report = new
                {
                    scenario = "phase3_single_pass_5s_800x360",
                    chat_prepare_ms = Math.Round(prepareStopwatch.Elapsed.TotalMilliseconds),
                    single_pass_encode_ms = Math.Round(encodeStopwatch.Elapsed.TotalMilliseconds),
                    total_ms = Math.Round(totalStopwatch.Elapsed.TotalMilliseconds),
                    cpu_ms = Math.Round(processCpuMilliseconds),
                    allocated_bytes = managedAllocatedBytes,
                    temporary_chat_video_created = false,
                    video_encode_processes = 1,
                    ssim_vs_two_pass = Math.Round(ssim, 6),
                    output_bytes = new FileInfo(outputPath).Length,
                    frame_metrics = progress.Messages.FirstOrDefault(x => x.Contains("phase=\"chat_frames\""))
                };
                _output.WriteLine(JsonSerializer.Serialize(report));
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        [Fact]
        [Trait("Category", "ProfileBenchmark")]
        public async Task SoftwareProfilesReportSpeedSizeAndQuality()
        {
            if (!TryResolveFfmpeg(out var ffmpegPath, out _))
                return;

            var directory = CreateTestDirectory();
            var sourcePath = Path.Combine(directory, "profile-source.mkv");
            try
            {
                await RunFixtureFfmpeg(
                    ffmpegPath,
                    directory,
                    "profile-source.log",
                    sourcePath,
                    new[]
                    {
                        "-y",
                        "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=5",
                        "-an", "-c:v", "ffv1", "-pix_fmt", "yuv420p",
                        sourcePath
                    });

                var results = new List<object>();
                foreach (var profile in Enum.GetValues<CombinedRenderSpeedProfile>())
                {
                    string outputPath = Path.Combine(directory, $"profile-{profile}.mp4");
                    var settings = CombinedEncoderResolver.ResolveFromAvailable(
                        CombinedRenderEncoder.Software,
                        profile,
                        new HashSet<string>());
                    var arguments = new List<string> { "-y", "-i", sourcePath, "-an" };
                    arguments.AddRange(settings.Arguments);
                    arguments.AddRange(new[] { "-pix_fmt", "yuv420p", outputPath });

                    var stopwatch = Stopwatch.StartNew();
                    await RunFixtureFfmpeg(
                        ffmpegPath,
                        directory,
                        $"profile-{profile}.log",
                        outputPath,
                        arguments);
                    stopwatch.Stop();

                    string ssimLogPath = Path.Combine(directory, $"profile-{profile}-ssim.log");
                    await FfmpegRunner.RunAsync(
                        ffmpegPath,
                        new[] { "-i", sourcePath, "-i", outputPath, "-lavfi", "ssim", "-f", "null", "-" },
                        directory,
                        ssimLogPath,
                        expectedOutputPath: null,
                        StubTaskProgress.Instance,
                        CancellationToken.None);
                    string ssimLog = await File.ReadAllTextAsync(ssimLogPath);
                    var match = Regex.Match(ssimLog, @"All:(?<score>\d+(?:\.\d+)?)");
                    Assert.True(match.Success, $"FFmpeg did not report SSIM for {profile}.");
                    double ssim = double.Parse(
                        match.Groups["score"].Value,
                        System.Globalization.CultureInfo.InvariantCulture);

                    results.Add(new
                    {
                        profile = profile.ToString(),
                        encode_ms = Math.Round(stopwatch.Elapsed.TotalMilliseconds),
                        output_bytes = new FileInfo(outputPath).Length,
                        ssim_vs_lossless_source = Math.Round(ssim, 6)
                    });
                }

                _output.WriteLine(JsonSerializer.Serialize(new
                {
                    scenario = "phase4_software_profiles_5s_1280x720",
                    profiles = results
                }));
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        private static Task RunFixtureFfmpeg(
            string ffmpegPath,
            string directory,
            string logName,
            string outputPath,
            IReadOnlyList<string> arguments)
        {
            return FfmpegRunner.RunAsync(
                ffmpegPath,
                arguments,
                directory,
                Path.Combine(directory, logName),
                outputPath,
                StubTaskProgress.Instance,
                CancellationToken.None);
        }

        private static ChatRenderOptions CreateOfflineChatOptions(
            string ffmpegPath,
            string directory,
            string inputPath,
            string outputPath,
            int width,
            int height,
            int frameRate)
        {
            return new ChatRenderOptions
            {
                InputFile = inputPath,
                OutputFile = outputPath,
                ChatWidth = width,
                ChatHeight = height,
                BackgroundColor = SKColor.Parse("#111111"),
                AlternateBackgroundColor = SKColor.Parse("#191919"),
                MessageColor = SKColors.White,
                Font = "Inter Embedded",
                FontSize = 12,
                MessageFontStyle = SKFontStyle.Normal,
                UsernameFontStyle = SKFontStyle.Bold,
                Framerate = frameRate,
                UpdateRate = 0.2,
                ChatBadges = false,
                BttvEmotes = false,
                FfzEmotes = false,
                StvEmotes = false,
                Offline = true,
                EmojiVendor = EmojiVendor.None,
                SkipDriveWaiting = true,
                FfmpegPath = ffmpegPath,
                TempFolder = directory,
                InputArgs = "-y -framerate {fps} -f rawvideo -pix_fmt {pix_fmt} -video_size {width}x{height} -i -",
                OutputArgs = "-c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{save_path}\""
            };
        }

        private static async Task<JsonDocument> ProbeAsync(
            string ffprobePath,
            string mediaPath,
            string workingDirectory)
        {
            using var process = new Process
            {
                StartInfo =
                {
                    FileName = ffprobePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = workingDirectory
                }
            };
            foreach (var argument in new[]
            {
                "-v", "error",
                "-show_entries", "format=start_time,duration:stream=codec_type,width,height,avg_frame_rate,start_time",
                "-of", "json",
                mediaPath
            })
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            Assert.True(process.Start());
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;

            Assert.True(process.ExitCode == 0, error);
            return JsonDocument.Parse(output);
        }

        private static double ParseSeconds(JsonElement element, string propertyName)
        {
            return double.Parse(
                element.GetProperty(propertyName).GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool TryResolveFfmpeg(out string ffmpegPath, out string ffprobePath)
        {
            ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";
            ffprobePath = ResolveFfprobePath(ffmpegPath);

            return CanStart(ffmpegPath, "-version") && CanStart(ffprobePath, "-version");
        }

        private static string ResolveFfprobePath(string ffmpegPath)
        {
            var directory = Path.GetDirectoryName(ffmpegPath);
            if (string.IsNullOrWhiteSpace(directory))
                return OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

            var filename = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
            return Path.Combine(directory, filename);
        }

        private static bool CanStart(string executable, string argument)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = argument,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (process is null)
                    return false;

                return process.WaitForExit(5_000) && process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static string CreateTestDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "TwitchDownloaderTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTestDirectory(string directory)
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        private sealed class BenchmarkTaskProgress : TwitchDownloaderCore.Interfaces.ITaskProgress
        {
            internal List<string> Messages { get; } = new();

            public void LogVerbose(string logMessage) => Messages.Add(logMessage);
            public void LogVerbose(System.Runtime.CompilerServices.DefaultInterpolatedStringHandler logMessage) => Messages.Add(logMessage.ToStringAndClear());
            public void LogInfo(string logMessage) => Messages.Add(logMessage);
            public void LogInfo(System.Runtime.CompilerServices.DefaultInterpolatedStringHandler logMessage) => Messages.Add(logMessage.ToStringAndClear());
            public void LogWarning(string logMessage) => Messages.Add(logMessage);
            public void LogWarning(System.Runtime.CompilerServices.DefaultInterpolatedStringHandler logMessage) => Messages.Add(logMessage.ToStringAndClear());
            public void LogError(string logMessage) => Messages.Add(logMessage);
            public void LogError(System.Runtime.CompilerServices.DefaultInterpolatedStringHandler logMessage) => Messages.Add(logMessage.ToStringAndClear());
            public void LogFfmpeg(string logMessage) { }
            public void SetStatus(string status) { }
            public void SetTemplateStatus(string status, int initialPercent) { }
            public void SetTemplateStatus(string status, int initialPercent, TimeSpan initialTime1, TimeSpan initialTime2) { }
            public void ReportProgress(int percent) { }
            public void ReportProgress(int percent, TimeSpan time1, TimeSpan time2) { }
        }
    }
}
