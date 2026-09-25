using SkiaSharp;
using TwitchDownloaderCore.ChatRender.Caching;

namespace TwitchDownloaderCore.Tests.CombinedRenderTests
{
    public sealed class RenderCacheTests
    {
        [Fact]
        public void TextMeasurementCacheRemainsBoundedUnderLongUniqueInput()
        {
            const int capacity = 32;
            var cache = new TextMeasurementCache(capacity);
            using var paint = new SKPaint { TextSize = 16 };

            for (int i = 0; i < 10_000; i++)
                cache.GetOrMeasure($"unique-message-{i}", paint, paint.TextSize, false);

            Assert.Equal(capacity, cache.Count);
            Assert.Equal(capacity, cache.Capacity);
        }

        [Fact]
        public void TextMeasurementCacheIncludesWidthAffectingPaintProperties()
        {
            var cache = new TextMeasurementCache(8);
            using var normalPaint = new SKPaint { TextSize = 20, TextScaleX = 1 };
            using var widePaint = new SKPaint { TextSize = 20, TextScaleX = 2 };

            float normalWidth = cache.GetOrMeasure("measurement", normalPaint, normalPaint.TextSize, false);
            float wideWidth = cache.GetOrMeasure("measurement", widePaint, widePaint.TextSize, false);

            Assert.Equal(2, cache.Count);
            Assert.True(wideWidth > normalWidth * 1.9f);
        }

        [Fact]
        public void TimestampCacheEvictsLeastRecentlyUsedBitmapAndCanvas()
        {
            using var cache = new BitmapCache(timestampCapacity: 2);
            var first = CreateTimestamp(cache, 1);
            var second = CreateTimestamp(cache, 2);
            cache.GetOrCreateCanvas(first);
            cache.GetOrCreateCanvas(second);

            var cachedFirst = cache.GetOrCreateTimestampBitmap(1, () => throw new InvalidOperationException()).bitmap;
            var third = CreateTimestamp(cache, 3);

            bool recreatedSecond = false;
            var replacementSecond = cache.GetOrCreateTimestampBitmap(
                2,
                () =>
                {
                    recreatedSecond = true;
                    return (new SKBitmap(4, 4), "2");
                }).bitmap;

            Assert.Equal(2, cache.TimestampBitmapCount);
            Assert.Equal(2, cache.TimestampCapacity);
            Assert.Same(first, cachedFirst);
            Assert.True(recreatedSecond);
            Assert.NotSame(second, replacementSecond);
            Assert.NotSame(third, replacementSecond);
            Assert.Equal(0, cache.CanvasCount);
        }

        [Fact]
        public void TimestampCacheRemainsBoundedAcrossHourLongSecondKeys()
        {
            const int capacity = 16;
            using var cache = new BitmapCache(timestampCapacity: capacity);

            for (int second = 0; second < 60 * 60; second++)
                _ = CreateTimestamp(cache, second);

            Assert.Equal(capacity, cache.TimestampBitmapCount);
        }

        private static SKBitmap CreateTimestamp(BitmapCache cache, int second)
        {
            return cache.GetOrCreateTimestampBitmap(
                second,
                () => (new SKBitmap(4, 4), second.ToString())).bitmap;
        }
    }
}
