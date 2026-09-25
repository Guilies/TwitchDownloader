using System.Globalization;
using SkiaSharp;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Tests.Fixtures;
using TwitchDownloaderCore.Tools;

namespace TwitchDownloaderCore.Tests.CombinedRenderTests
{
    public sealed partial class FfmpegIntegrationTests
    {
        [Theory]
        [Trait("Category", "Integration")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GifAnimationSurvivesBothCombinedEncodingPaths(bool singlePass)
        {
            if (!TryResolveFfmpeg(out var ffmpeg, out var ffprobe))
                return;

            var directory = CreateTestDirectory();
            try
            {
                var vodPath = Path.Combine(directory, "vod.mp4");
                var outputPath = Path.Combine(directory, "combined.mp4");
                await RunFixtureFfmpeg(ffmpeg, directory, "vod.log", vodPath, new[]
                {
                    "-y", "-f", "lavfi", "-i", "color=c=green:size=320x160:rate=20",
                    "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
                    "-t", "1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-shortest", vodPath
                });

                var options = GifChatFixture.Options(directory);
                options.FfmpegPath = ffmpeg;
                using var renderer = new ChatRenderer(options, StubTaskProgress.Instance);
                renderer.SetChatRoot(GifChatFixture.Create());
                await renderer.PrepareAsync(CancellationToken.None);
                Assert.Equal(20, renderer.EffectiveFramerate);
                var scaling = CombinedRenderPlanner.CalculateScaling(new Resolution(320, 160), 520, 160, 200, 160);
                var filter = CombinedRenderPlanner.BuildFilterComplex(scaling, new VideoFrameRate(20, 1));
                if (singlePass)
                {
                    var arguments = CombinedRenderPlanner.BuildSinglePassCompositeArguments(
                        vodPath, 200, 160, renderer.EffectiveFramerate,
                        SKImageInfo.PlatformColorType == SKColorType.Bgra8888 ? "bgra" : "rgba",
                        filter, outputPath,
                        CombinedEncoderResolver.ResolveFromAvailable(CombinedRenderEncoder.Software,
                            CombinedRenderSpeedProfile.Balanced, new HashSet<string>()), 0);
                    await FfmpegRunner.RunWithInputAsync(ffmpeg, arguments, directory,
                        Path.Combine(directory, "combined.log"), outputPath, StubTaskProgress.Instance,
                        (stream, token) => renderer.RenderRawFramesAsync(stream, outputPath, token), CancellationToken.None);
                }
                else
                {
                    await renderer.RenderVideoAsync(CancellationToken.None);
                    await RunFixtureFfmpeg(ffmpeg, directory, "combined.log", outputPath,
                        CombinedRenderPlanner.BuildCompositeArguments(vodPath, options.OutputFile, filter, outputPath, 0));
                }

                using var probe = await ProbeAsync(ffprobe, outputPath, directory);
                var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                var video = Assert.Single(streams, s => s.GetProperty("codec_type").GetString() == "video");
                Assert.Equal(520, video.GetProperty("width").GetInt32());
                Assert.Equal(160, video.GetProperty("height").GetInt32());
                Assert.Equal("20/1", video.GetProperty("avg_frame_rate").GetString());
                Assert.Single(streams, s => s.GetProperty("codec_type").GetString() == "audio");
                Assert.InRange(ParseSeconds(probe.RootElement.GetProperty("format"), "duration"), 0.99, 1.1);

                // Decode the actual MP4, checking GIF pixels after the fractional 0.25s chat trim.
                foreach (var (time, blue) in new[] { (0.05, true), (0.4, false) })
                {
                    var imagePath = Path.Combine(directory, blue ? "blue.png" : "red.png");
                    await RunFixtureFfmpeg(ffmpeg, directory, blue ? "blue.log" : "red.log", imagePath, new[]
                    {
                        "-y", "-ss", time.ToString(CultureInfo.InvariantCulture), "-i", outputPath,
                        "-frames:v", "1", imagePath
                    });
                    using var frame = SKBitmap.Decode(imagePath);
                    var pixel = frame.GetPixel(320 + options.SidePadding + 20, 100);
                    Assert.True(blue ? pixel.Blue > 200 && pixel.Red < 30 : pixel.Red > 200 && pixel.Blue < 30,
                        $"Unexpected GIF pixel at {time}s: {pixel}");
                    Assert.True(frame.GetPixel(100, 80).Green > 80, "The VOD pane should remain visible.");
                }
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }
    }
}
