using System.Text.Json;
using SkiaSharp;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.ChatRender.Caching;
using TwitchDownloaderCore.ChatRender.Processing;
using TwitchDownloaderCore.Tests.Fixtures;
using TwitchDownloaderCore.TwitchObjects;
using TwitchDownloaderCore.TwitchObjects.Gql;

namespace TwitchDownloaderCore.Tests.CombinedRenderTests
{
    public sealed class GifMessageTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "gif-chat-tests", Guid.NewGuid().ToString("N"));

        public GifMessageTests() => Directory.CreateDirectory(_directory);

        [Fact]
        public async Task GifMetadataAndBytesSurviveArchiveRoundTripAndCloning()
        {
            var chat = GifChatFixture.Create();
            var path = Path.Combine(_directory, "chat.json");
            await using (var stream = File.Create(path))
                await ChatJson.SerializeAsync(stream, chat, CancellationToken.None);
            var loaded = await ChatJson.DeserializeAsync(path);
            var original = loaded.comments[0].message.fragments[0].gif;
            var clone = loaded.comments[0].Clone();
            Assert.Equal(GifChatFixture.Url, original.url);
            Assert.Equal(chat.embeddedData.gifs[0].data, loaded.embeddedData.gifs[0].data);
            clone.message.fragments[0].gif.url = "changed";
            Assert.Equal(GifChatFixture.Url, original.url);
        }

        [Fact]
        public void ConversionPreservesSuppliedGifWithoutText()
        {
            // Synthetic source payload: this tests preservation, not current VOD API availability.
            var source = JsonSerializer.Deserialize<CommentVideo>(
                "{\"id\":\"1\",\"comments\":{\"edges\":[{\"node\":{\"id\":\"1\",\"commenter\":{\"id\":\"1\",\"displayName\":\"User\",\"login\":\"user\"},\"message\":{\"fragments\":[{\"gif\":{\"id\":\"fixture\",\"url\":\"" + GifChatFixture.Url + "\"}}],\"userBadges\":[]}}}]}}");
            var downloader = new ChatDownloader(new ChatDownloadOptions
            {
                DownloadFormat = ChatFormat.Json, TempFolder = _directory
            }, StubTaskProgress.Instance);
            var comment = Assert.Single(downloader.ConvertComments(source!, DateTime.MinValue));
            Assert.Equal("[GIF]", comment.message.body);
            Assert.Equal(GifChatFixture.Url, Assert.Single(comment.message.fragments).gif.url);
        }

        [Theory]
        [InlineData("https://media.giphy.com/media/abc/giphy.gif", true)]
        [InlineData("https://media2.giphy.com/media/v1.foo/abc/200.webp?cid=123", true)]
        [InlineData("https://media.giphy.com.evil.example/media/a/giphy.gif", false)]
        [InlineData("http://media.giphy.com/media/a/giphy.gif", false)]
        [InlineData("file:///C:/image.gif", false)]
        [InlineData("https://media.giphy.com:999/media/a/giphy.gif", false)]
        [InlineData("https://user@media.giphy.com/media/a/giphy.gif", false)]
        [InlineData("https://media.giphy.com/media/a/giphy.mp4", false)]
        [InlineData(null, false)]
        public void OnlyGiphyImageUrlsAreFetched(string? url, bool valid)
        {
            var gif = new ChatGif { id = "../../escape", url = url };
            Assert.Equal(valid, gif.TryGetAsset(out var key, out _));
            if (valid) Assert.Matches("^[A-F0-9]{64}$", key);
        }

        [Fact]
        public async Task OfflineAssetsAreDeduplicatedAndUnreferencedEmbedsAreIgnored()
        {
            var chat = GifChatFixture.Create();
            chat.comments.Add(chat.comments[0].Clone());
            chat.embeddedData.gifs.Add(new EmbedEmoteData { id = "unused", data = new byte[] { 1 } });
            var images = await GifImages.FetchAsync(chat, _directory, StubTaskProgress.Instance, true, CancellationToken.None);
            using var gif = Assert.Single(images).Value;
            Assert.Equal(2, gif.FrameCount);
            Assert.Equal(new[] { 20, 40 }, gif.EmoteFrameDurations);
            Assert.Equal(SKColors.Red, gif.EmoteFrames[0].GetPixel(20, 20));
            Assert.Equal(SKColors.Blue, gif.EmoteFrames[1].GetPixel(20, 20));
        }

        [Fact]
        public async Task OfflineCacheWorksAndCorruptEmbedFallsBackToCache()
        {
            var chat = GifChatFixture.Create();
            var embed = chat.embeddedData.gifs[0];
            Directory.CreateDirectory(Path.Combine(_directory, "gifs"));
            await File.WriteAllBytesAsync(Path.Combine(_directory, "gifs", embed.id + "_1.gif"), embed.data);
            embed.data = new byte[] { 1, 2, 3 };
            var images = await GifImages.FetchAsync(chat, _directory, StubTaskProgress.Instance, true, CancellationToken.None);
            using var gif = Assert.Single(images).Value;
            Assert.Equal(2, gif.FrameCount);
        }

        [Fact]
        public async Task CancellationPropagates()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GifImages.FetchAsync(
                GifChatFixture.Create(), _directory, StubTaskProgress.Instance, true, cancellation.Token));
        }

        [Theory]
        [InlineData(40, 120, 96, 64)]
        [InlineData(200, 80, 96, 64)]
        [InlineData(200, 160, 1, 300)]
        [InlineData(200, 160, 1000, 1)]
        public async Task GifScalingFitsNarrowAndShortChatWithoutZeroSizedBitmaps(
            int width, int height, ushort gifWidth, ushort gifHeight)
        {
            var chat = GifChatFixture.Create();
            chat.embeddedData.gifs[0].data = GifChatFixture.CreateGif(gifWidth, gifHeight);
            var options = GifChatFixture.Options(_directory, width, height);
            var fetcher = new ImageFetcher(_directory, options, StubTaskProgress.Instance, Array.Empty<string>());
            var images = await fetcher.FetchAllImagesAsync(chat, CancellationToken.None);
            using var cache = new ImageCache();
            cache.Initialize(images.Badges, images.Emotes, images.ThirdPartyEmotes, images.Cheermotes,
                images.Emojis, images.Avatars, images.Gifs);
            var gif = Assert.Single(cache.Gifs).Value;
            Assert.InRange(gif.Width, 1, width - 2 * options.SidePadding - options.AccentIndentWidth);
            int reservedHeight = (gif.Height + options.SectionHeight - 1) / options.SectionHeight * options.SectionHeight;
            Assert.True(reservedHeight + options.SectionHeight + options.VerticalPadding <= height);
        }

        [Fact]
        public async Task TextFollowingGifStartsBelowItsReservedRows()
        {
            var chat = GifChatFixture.Create();
            chat.comments[0].message.fragments.Add(new Fragment { text = "following text" });
            var options = GifChatFixture.Options(_directory);
            using var renderer = new ChatRenderer(options, StubTaskProgress.Instance);
            renderer.SetChatRoot(chat);
            using var frames = new MemoryStream();
            await renderer.RenderRawFramesAsync(frames, options.OutputFile, CancellationToken.None);
            using var frame = Bitmap(frames.GetBuffer(), 0, options);
            Assert.Equal(96 * 64, frame.Pixels.Count(pixel => pixel == SKColors.Blue));
            Assert.Contains(frame.Pixels.Skip(130 * options.ChatWidth), pixel => pixel == SKColors.White);
        }

        [Fact]
        public async Task HtmlExportEmbedsGifAndEscapesFallbackLabel()
        {
            var chat = GifChatFixture.Create();
            chat.comments[0].message.fragments[0].text = "GIF \"<label>\"";
            using var stream = new MemoryStream();
            await ChatHtml.SerializeAsync(stream, "chat.html", chat, StubTaskProgress.Instance, true);
            var html = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            Assert.Contains("data:image/gif;base64,", html);
            Assert.Contains("&lt;label&gt;", html);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RawCombinedFramesAnimateWithReservedLayoutAndKeepFrameRate(bool mask)
        {
            var chat = GifChatFixture.Create();
            var options = GifChatFixture.Options(_directory);
            options.GenerateMask = mask;
            using var renderer = new ChatRenderer(options, StubTaskProgress.Instance);
            renderer.SetChatRoot(chat);
            using var frames = new MemoryStream();
            await renderer.RenderRawFramesAsync(frames, options.OutputFile, CancellationToken.None);
            Assert.Equal(20, renderer.EffectiveFramerate);
            int bytesPerFrame = options.ChatWidth * options.ChatHeight * 4;
            Assert.Equal(20 * bytesPerFrame, frames.Length);
            using var first = Bitmap(frames.GetBuffer(), 0, options);
            using var later = Bitmap(frames.GetBuffer(), 8 * bytesPerFrame, options);
            Assert.Equal(SKColors.Blue, first.GetPixel(options.SidePadding + 20, 100));
            Assert.Equal(SKColors.Red, later.GetPixel(options.SidePadding + 20, 100));
            // The complete GIF lies below the username. All 96x64 pixels remain visible.
            Assert.Equal(96 * 64, first.Pixels.Count(c => c == SKColors.Blue));
            Assert.Equal(96 * 64, later.Pixels.Count(c => c == SKColors.Red));
        }

        [Fact]
        public async Task MissingGifUsesLabelAndMayReduceStaticFrameRate()
        {
            var chat = GifChatFixture.Create();
            chat.embeddedData.gifs.Clear();
            var options = GifChatFixture.Options(_directory);
            using var renderer = new ChatRenderer(options, StubTaskProgress.Instance);
            renderer.SetChatRoot(chat);
            using var frames = new MemoryStream();
            await renderer.RenderRawFramesAsync(frames, options.OutputFile, CancellationToken.None);
            Assert.Equal(10, renderer.EffectiveFramerate);
            using var frame = Bitmap(frames.GetBuffer(), 0, options);
            Assert.Contains(frame.Pixels, pixel => pixel == SKColors.White);
            Assert.DoesNotContain(frame.Pixels, pixel => pixel == SKColors.Blue);
        }

        [Fact]
        public async Task AnimationAdvancesAtExactFrameBoundariesAndLoops()
        {
            var options = GifChatFixture.Options(_directory);
            using var renderer = new ChatRenderer(options, StubTaskProgress.Instance);
            renderer.SetChatRoot(GifChatFixture.Create(0, 1));
            using var frames = new MemoryStream();
            await renderer.RenderRawFramesAsync(frames, options.OutputFile, CancellationToken.None);
            int frameBytes = options.ChatWidth * options.ChatHeight * 4;
            foreach (var (index, color) in new[] { (0, SKColors.Red), (4, SKColors.Blue), (12, SKColors.Red), (16, SKColors.Blue) })
            {
                using var bitmap = Bitmap(frames.GetBuffer(), index * frameBytes, options);
                Assert.Equal(color, bitmap.GetPixel(options.SidePadding + 20, 100));
            }
        }

        private static SKBitmap Bitmap(byte[] bytes, int offset, ChatRenderOptions options)
        {
            var bitmap = new SKBitmap(options.ChatWidth, options.ChatHeight);
            System.Runtime.InteropServices.Marshal.Copy(bytes, offset, bitmap.GetPixels(), bitmap.ByteCount);
            return bitmap;
        }

        public void Dispose() => Directory.Delete(_directory, true);
    }
}
