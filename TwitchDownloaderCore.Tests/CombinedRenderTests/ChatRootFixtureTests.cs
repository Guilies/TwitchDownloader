using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Tests.Fixtures;

namespace TwitchDownloaderCore.Tests.CombinedRenderTests
{
    public sealed class ChatRootFixtureTests
    {
        [Fact]
        public async Task DeterministicFixtureRoundTripsWithFractionalTimeline()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "TwitchDownloaderTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "chat.json");

            try
            {
                var fixture = CombinedRenderFixtureFactory.CreateChatRoot(1.25, 2.75);
                await using (var stream = File.Create(path))
                {
                    await ChatJson.SerializeAsync(stream, fixture, CancellationToken.None);
                }

                var deserialized = await ChatJson.DeserializeAsync(path, cancellationToken: CancellationToken.None);

                Assert.Equal(1.25, deserialized.video.start);
                Assert.Equal(2.75, deserialized.video.end);
                Assert.Equal(3, deserialized.comments.Count);
                Assert.Equal(1.375, deserialized.comments[0].content_offset_seconds);
                Assert.Equal("Final fixture message", deserialized.comments[^1].message.body);
                Assert.Empty(deserialized.embeddedData.firstParty);
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }
    }
}
