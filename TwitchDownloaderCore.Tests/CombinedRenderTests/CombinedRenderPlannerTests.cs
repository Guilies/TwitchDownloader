using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Tools;

namespace TwitchDownloaderCore.Tests.CombinedRenderTests
{
    public class CombinedRenderPlannerTests
    {
        [Fact]
        public void QualityResolutionIsCaseInsensitiveAndPreservesNtscRate()
        {
            var m3u8 = new M3U8(new M3U8.Metadata(), new[]
            {
                CreateStream("chunked", "Source", 1920, 1080, 59.94m, "source.m3u8"),
                CreateStream("720p30", "720p", 1280, 720, 29.97m, "720p.m3u8")
            });

            var qualities = VideoQualities.FromM3U8(m3u8);
            var selected = CombinedRenderPlanner.ResolveVideoQuality(qualities, "720P");

            Assert.Equal("720p30", selected.Name);
            Assert.Equal(new Resolution(1280, 720), selected.Resolution);
            Assert.Equal(new VideoFrameRate(30_000, 1_001), selected.FrameRate);
            Assert.Equal("30000/1001", selected.FrameRate.FfmpegExpression);
            Assert.Equal("720p.m3u8", selected.Playlist.Path);
        }

        [Fact]
        public void UnknownQualityFallsBackToSourceUsingTheSameQualityRulesAsDownloader()
        {
            var m3u8 = new M3U8(new M3U8.Metadata(), new[]
            {
                CreateStream("chunked", "Source", 1920, 1080, 60m, "source.m3u8"),
                CreateStream("720p30", "720p", 1280, 720, 30m, "720p.m3u8")
            });

            var selected = CombinedRenderPlanner.ResolveVideoQuality(
                VideoQualities.FromM3U8(m3u8),
                "not-a-quality");

            Assert.Equal("1080p60", selected.Name);
            Assert.Equal(new Resolution(1920, 1080), selected.Resolution);
            Assert.Equal(new VideoFrameRate(60, 1), selected.FrameRate);
        }

        [Fact]
        public void AudioOnlyQualityIsRejectedForCombinedRendering()
        {
            var audioStream = new M3U8.Stream(
                new M3U8.Stream.ExtMediaInfo(
                    M3U8.Stream.ExtMediaInfo.MediaType.Video,
                    "audio_only",
                    "Audio Only",
                    false,
                    false),
                new M3U8.Stream.ExtStreamInfo(
                    0,
                    1,
                    new[] { "mp4a.40.2" },
                    (0, 0),
                    "audio_only",
                    0),
                "audio.m3u8");
            var qualities = VideoQualities.FromM3U8(
                new M3U8(new M3U8.Metadata(), new[] { audioStream }));

            Assert.Throws<InvalidOperationException>(
                () => CombinedRenderPlanner.ResolveVideoQuality(qualities, "audio"));
        }

        [Fact]
        public void LandscapeVideoIsCenteredInTheVodCell()
        {
            var scaling = CombinedRenderPlanner.CalculateScaling(
                new Resolution(1920, 1080),
                outputWidth: 1920,
                outputHeight: 1080,
                chatWidth: 480,
                chatHeight: 1080);

            Assert.Equal(1440, scaling.VodCellWidth);
            Assert.Equal(1440, scaling.ScaledVodWidth);
            Assert.Equal(810, scaling.ScaledVodHeight);
            Assert.Equal(0, scaling.VodPaddingLeft);
            Assert.Equal(135, scaling.LetterboxPaddingTop);
            Assert.Equal(1920, scaling.VodCellWidth + scaling.ChatWidth);
        }

        [Fact]
        public void PortraitVideoIsPaddedToAnExactOutputWidth()
        {
            var scaling = CombinedRenderPlanner.CalculateScaling(
                new Resolution(1080, 1920),
                outputWidth: 1920,
                outputHeight: 1080,
                chatWidth: 480,
                chatHeight: 1080);

            Assert.Equal(606, scaling.ScaledVodWidth);
            Assert.Equal(1080, scaling.ScaledVodHeight);
            Assert.Equal(417, scaling.VodPaddingLeft);
            Assert.Equal(417, scaling.VodPaddingRight);
            Assert.True(scaling.RequiresPillarboxing);
        }

        [Fact]
        public void FilterScalesBeforeFrameRateNormalizationAndLabelsOutput()
        {
            var scaling = CombinedRenderPlanner.CalculateScaling(
                new Resolution(1920, 1080), 1920, 1080, 480, 1080);

            var filter = CombinedRenderPlanner.BuildFilterComplex(
                scaling,
                new VideoFrameRate(60_000, 1_001));

            Assert.Equal(
                "[0:v]scale=1440:810:flags=lanczos,pad=1440:1080:0:135:black," +
                "setsar=1,fps=60000/1001[vod];" +
                "[1:v]setsar=1,fps=60000/1001[chat];" +
                "[vod][chat]hstack=inputs=2[outv]",
                filter);
        }

        [Fact]
        public void CombinedRenderUsesExactTrimByDefault()
        {
            Assert.Equal(VideoTrimMode.Exact, new CombinedRenderOptions().TrimMode);
        }

        [Fact]
        public void TimelineResolvesOneExactIntervalForBothInputs()
        {
            var options = new CombinedRenderOptions
            {
                TrimBeginning = true,
                TrimBeginningTime = TimeSpan.FromSeconds(1.25),
                TrimEnding = true,
                TrimEndingTime = TimeSpan.FromSeconds(9.75)
            };

            var timeline = CombinedRenderPlanner.ResolveTimeline(options, TimeSpan.FromSeconds(20));

            Assert.Equal(TimeSpan.FromSeconds(1.25), timeline.Start);
            Assert.Equal(TimeSpan.FromSeconds(9.75), timeline.End);
            Assert.Equal(TimeSpan.FromSeconds(8.5), timeline.Duration);
            Assert.True(timeline.TrimBeginning);
            Assert.True(timeline.TrimEnding);
        }

        [Fact]
        public void TimelineRejectsAnEndBeyondTheVod()
        {
            var options = new CombinedRenderOptions
            {
                TrimEnding = true,
                TrimEndingTime = TimeSpan.FromSeconds(21)
            };

            Assert.Throws<ArgumentOutOfRangeException>(
                () => CombinedRenderPlanner.ResolveTimeline(options, TimeSpan.FromSeconds(20)));
        }

        [Fact]
        public void TrimmedCombinedRenderRejectsSafeMode()
        {
            var options = new CombinedRenderOptions
            {
                Id = 1,
                OutputFile = "output.mp4",
                TrimBeginning = true,
                TrimBeginningTime = TimeSpan.FromSeconds(1),
                TrimMode = VideoTrimMode.Safe
            };

            Assert.Contains("exact trim mode", options.Validate());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(11)]
        public void CombinedRenderBoundsChatDownloadConcurrency(int threadCount)
        {
            var options = new CombinedRenderOptions
            {
                Id = 1,
                OutputFile = "output.mp4",
                ChatDownloadThreads = threadCount
            };

            Assert.Contains("Chat download threads", options.Validate());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(21)]
        public void CombinedRenderBoundsVodDownloadConcurrency(int threadCount)
        {
            var options = new CombinedRenderOptions
            {
                Id = 1,
                OutputFile = "output.mp4",
                DownloadThreads = threadCount
            };

            Assert.Contains("VOD download threads", options.Validate());
        }

        [Fact]
        public void CompositeArgumentsMapFilteredVideoAndOptionalVodAudio()
        {
            var arguments = CombinedRenderPlanner.BuildCompositeArguments(
                "vod.mp4",
                "chat.mp4",
                "filter",
                "output.mp4",
                4);

            AssertContainsSequence(arguments, "-map", "[outv]");
            AssertContainsSequence(arguments, "-map", "0:a?");
            AssertContainsSequence(arguments, "-threads", "4");
            Assert.DoesNotContain("-r", arguments);
            Assert.DoesNotContain("-vsync", arguments);
            Assert.Equal("output.mp4", arguments[^1]);
        }

        [Fact]
        public void SinglePassArgumentsConsumeRawChatFromStandardInput()
        {
            var arguments = CombinedRenderPlanner.BuildSinglePassCompositeArguments(
                "vod.mp4",
                480,
                1080,
                5,
                "bgra",
                "filter",
                "output.mp4",
                CombinedEncoderResolver.ResolveFromAvailable(
                    CombinedRenderEncoder.Software,
                    CombinedRenderSpeedProfile.Balanced,
                    new HashSet<string>()),
                4);

            AssertContainsSequence(arguments, "-i", "vod.mp4");
            AssertContainsSequence(arguments, "-f", "rawvideo");
            AssertContainsSequence(arguments, "-framerate", "5");
            AssertContainsSequence(arguments, "-video_size", "480x1080");
            AssertContainsSequence(arguments, "-i", "pipe:0");
            AssertContainsSequence(arguments, "-progress", "pipe:2");
            Assert.Contains("-nostats", arguments);
            AssertContainsSequence(arguments, "-map", "0:a?");
            Assert.Equal(1, arguments.Count(x => x == "-c:v"));
            Assert.DoesNotContain("chat.mp4", arguments);
        }

        [Theory]
        [InlineData(CombinedRenderSpeedProfile.Fast, "veryfast", "24")]
        [InlineData(CombinedRenderSpeedProfile.Balanced, "medium", "23")]
        [InlineData(CombinedRenderSpeedProfile.Quality, "slow", "20")]
        public void SoftwareProfilesHaveTypedPresetAndQualityValues(
            CombinedRenderSpeedProfile profile,
            string expectedPreset,
            string expectedCrf)
        {
            var encoder = CombinedEncoderResolver.ResolveFromAvailable(
                CombinedRenderEncoder.Software,
                profile,
                new HashSet<string>());

            Assert.False(encoder.IsHardware);
            AssertContainsSequence(encoder.Arguments.ToList(), "-preset", expectedPreset);
            AssertContainsSequence(encoder.Arguments.ToList(), "-crf", expectedCrf);
        }

        [Fact]
        public void UnsupportedHardwareFallsBackToSoftware()
        {
            var encoder = CombinedEncoderResolver.ResolveFromAvailable(
                CombinedRenderEncoder.NvidiaNvenc,
                CombinedRenderSpeedProfile.Balanced,
                new HashSet<string>());

            Assert.Equal(CombinedRenderEncoder.Software, encoder.Encoder);
            Assert.Equal("libx264", encoder.FfmpegEncoder);
            Assert.True(encoder.FellBackToSoftware);
        }

        [Fact]
        public void HardwareProbeUsesNvencCompatibleFrameDimensionsAndExactProfileArguments()
        {
            var encoder = CombinedEncoderResolver.ResolveFromAvailable(
                CombinedRenderEncoder.NvidiaNvenc,
                CombinedRenderSpeedProfile.Balanced,
                new HashSet<string> { "h264_nvenc" });

            var arguments = CombinedEncoderResolver.BuildProbeArguments(encoder);

            Assert.True(CombinedEncoderResolver.HardwareProbeDimension >= 256);
            AssertContainsSequence(
                arguments,
                "-i",
                $"color=c=black:s={CombinedEncoderResolver.HardwareProbeDimension}x{CombinedEncoderResolver.HardwareProbeDimension}:d=0.1");
            AssertContainsSequence(arguments, "-c:v", "h264_nvenc");
            AssertContainsSequence(arguments, "-preset", "p5");
            AssertContainsSequence(arguments, "-cq", "23");
        }

        [Fact]
        public void AutoHardwareUsesFirstFunctionallyAvailableEncoderAndOmitsCodecThreads()
        {
            var encoder = CombinedEncoderResolver.ResolveFromAvailable(
                CombinedRenderEncoder.AutoHardware,
                CombinedRenderSpeedProfile.Fast,
                new HashSet<string> { "h264_amf", "h264_qsv" });
            var arguments = CombinedRenderPlanner.BuildSinglePassCompositeArguments(
                "vod.mp4",
                480,
                1080,
                5,
                "bgra",
                "filter",
                "output.mp4",
                encoder,
                12);

            Assert.Equal(CombinedRenderEncoder.IntelQuickSync, encoder.Encoder);
            AssertContainsSequence(arguments, "-c:v", "h264_qsv");
            Assert.DoesNotContain("-threads", arguments);
            AssertContainsSequence(arguments, "-filter_complex_threads", "12");
        }

        [Fact]
        public void FfmpegProgressParserUsesMachineReadableTimestampsMonotonically()
        {
            var parser = new FfmpegProgressParser(TimeSpan.FromSeconds(10));

            Assert.False(parser.TryParse("out_time_us=2500000", out _));
            Assert.True(parser.TryParse("progress=continue", out var first));
            Assert.Equal(25, first.Percent);

            Assert.False(parser.TryParse("out_time_us=1000000", out _));
            Assert.True(parser.TryParse("progress=continue", out var nonRegressing));
            Assert.Equal(25, nonRegressing.Percent);

            Assert.False(parser.TryParse("out_time=00:00:07.500000", out _));
            Assert.False(parser.TryParse("speed=1.5x", out _));
            Assert.False(parser.TryParse("frame=225", out _));
            Assert.False(parser.TryParse("total_size=123456", out _));
            Assert.True(parser.TryParse("progress=continue", out var second));
            Assert.Equal(75, second.Percent);
            Assert.Equal(1.5, second.Speed);
            Assert.Equal(225, second.Frame);
            Assert.Equal(123456, second.TotalSize);
            Assert.Equal(TimeSpan.FromSeconds(7.5), second.OutputTime);
            Assert.Equal(TimeSpan.FromSeconds(10), second.Duration);
            Assert.NotNull(second.Remaining);
            Assert.InRange(second.Remaining!.Value.TotalSeconds, 1.66, 1.67);

            Assert.True(parser.TryParse("progress=end", out var completed));
            Assert.Equal(100, completed.Percent);
        }

        private static M3U8.Stream CreateStream(
            string groupId,
            string name,
            uint width,
            uint height,
            decimal frameRate,
            string path)
        {
            return new M3U8.Stream(
                new M3U8.Stream.ExtMediaInfo(
                    M3U8.Stream.ExtMediaInfo.MediaType.Video,
                    groupId,
                    name,
                    true,
                    true),
                new M3U8.Stream.ExtStreamInfo(
                    0,
                    1,
                    new[] { "avc1.4D401F", "mp4a.40.2" },
                    (width, height),
                    groupId,
                    frameRate),
                path);
        }

        private static void AssertContainsSequence(
            IList<string> arguments,
            string first,
            string second)
        {
            for (var index = 0; index + 1 < arguments.Count; index++)
            {
                if (arguments[index] == first && arguments[index + 1] == second)
                    return;
            }

            Assert.Fail($"The sequence '{first}', '{second}' was not found.");
        }
    }
}
