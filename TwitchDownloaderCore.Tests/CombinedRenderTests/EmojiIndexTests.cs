using TwitchDownloaderCore.ChatRender.Message;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Models;

namespace TwitchDownloaderCore.Tests.CombinedRenderTests
{
    public sealed class EmojiIndexTests
    {
        [Fact]
        public void FindsExactEmojiWithoutScanningTheCatalog()
        {
            var emoji = EmojiIndex.Find("😀");

            Assert.NotNull(emoji);
            Assert.Equal("😀", emoji.Value.Sequence.AsString);
        }

        [Fact]
        public void NonEmojiGraphemeDoesNotMatch()
        {
            Assert.Null(EmojiIndex.Find("é"));
        }

        [Theory]
        [InlineData(EmojiVendor.TwitterTwemoji)]
        [InlineData(EmojiVendor.GoogleNotoColor)]
        [Trait("Category", "Integration")]
        public async Task OnlyRequestedEmojiAssetIsDecoded(EmojiVendor vendor)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "TwitchDownloaderTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var emojis = await TwitchHelper.GetEmojis(
                    directory,
                    vendor,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1F600" },
                    StubTaskProgress.Instance,
                    CancellationToken.None);

                var bitmap = Assert.Single(emojis);
                Assert.Equal("1F600", bitmap.Key, ignoreCase: true);
                bitmap.Value.Dispose();

                Assert.Single(Directory.EnumerateFiles(directory, "*.png", SearchOption.AllDirectories));
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }
    }
}
