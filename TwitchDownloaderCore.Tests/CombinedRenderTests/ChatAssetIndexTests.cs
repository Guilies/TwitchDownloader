using TwitchDownloaderCore.ChatRender.Processing;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Tests.CombinedRenderTests
{
    public sealed class ChatAssetIndexTests
    {
        [Fact]
        public void BuildsOneExactTokenSetAcrossAllMessages()
        {
            var comments = new[]
            {
                CreateComment("hello Kappa world"),
                CreateComment("Kappa\tOMEGALUL"),
                CreateComment("Kappa! is punctuation-distinct")
            };

            var tokens = ChatAssetIndex.BuildWhitespaceDelimitedTokens(comments);

            Assert.Contains("Kappa", tokens);
            Assert.Contains("OMEGALUL", tokens);
            Assert.Contains("Kappa!", tokens);
            Assert.Equal(7, tokens.Count);
        }

        private static Comment CreateComment(string body)
        {
            return new Comment
            {
                message = new Message { body = body }
            };
        }
    }
}
