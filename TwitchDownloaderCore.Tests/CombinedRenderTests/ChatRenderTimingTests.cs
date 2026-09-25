using TwitchDownloaderCore.ChatRender.Core;
using TwitchDownloaderCore.Options;

namespace TwitchDownloaderCore.Tests.CombinedRenderTests
{
    public class ChatRenderTimingTests
    {
        [Theory]
        [InlineData(30, 0.2, 6)]
        [InlineData(60, 0.2, 12)]
        [InlineData(30, 1.0, 30)]
        [InlineData(30, 5.0, 150)]
        [InlineData(30, 0.01, 1)]
        public void UpdateFrameRepresentsSecondsBetweenVisualUpdates(
            int frameRate,
            double updateRate,
            int expectedFrames)
        {
            var options = new ChatRenderOptions
            {
                Framerate = frameRate,
                UpdateRate = updateRate
            };

            Assert.Equal(expectedFrames, options.UpdateFrame);
        }

        [Fact]
        public void FractionalTrimIsConvertedAtFramePrecision()
        {
            var result = ChatRenderTimeline.CalculateTicks(1.25, 2.75, 30);

            Assert.Equal(37, result.StartTick);
            Assert.Equal(46, result.TotalTicks);
        }

        [Theory]
        [InlineData(-0.1, 1.0, 30)]
        [InlineData(2.0, 1.0, 30)]
        [InlineData(0.0, 1.0, 0)]
        public void InvalidTimelineIsRejected(double start, double end, int frameRate)
        {
            Assert.ThrowsAny<ArgumentOutOfRangeException>(
                () => ChatRenderTimeline.CalculateTicks(start, end, frameRate));
        }

        [Theory]
        [InlineData(30, 0.2, 5)]
        [InlineData(60, 0.2, 5)]
        [InlineData(30, 0.3, 10)]
        [InlineData(30, 1.0, 1)]
        [InlineData(30, 5.0, 1)]
        [InlineData(29, 0.2, 29)]
        public void StaticFrameRatePreservesUpdateGrid(
            int requestedFrameRate,
            double updateRate,
            int expectedStaticFrameRate)
        {
            int staticFrameRate = ChatRenderFrameRatePlanner.GetStaticFrameRate(
                requestedFrameRate,
                updateRate);
            var original = new ChatRenderOptions
            {
                Framerate = requestedFrameRate,
                UpdateRate = updateRate
            };
            var reduced = new ChatRenderOptions
            {
                Framerate = staticFrameRate,
                UpdateRate = updateRate
            };

            Assert.Equal(expectedStaticFrameRate, staticFrameRate);
            Assert.Equal(
                original.UpdateFrame / (double)original.Framerate,
                reduced.UpdateFrame / (double)reduced.Framerate,
                precision: 10);
        }

        [Theory]
        [InlineData(0, 0.2)]
        [InlineData(30, 0)]
        [InlineData(30, double.NaN)]
        public void InvalidStaticFrameRateInputsAreRejected(int frameRate, double updateRate)
        {
            Assert.ThrowsAny<ArgumentOutOfRangeException>(
                () => ChatRenderFrameRatePlanner.GetStaticFrameRate(frameRate, updateRate));
        }
    }
}
