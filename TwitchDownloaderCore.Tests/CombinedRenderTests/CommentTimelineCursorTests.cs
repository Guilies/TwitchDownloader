using TwitchDownloaderCore.ChatRender.Core;
using TwitchDownloaderCore.ChatRender.Processing;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Tests.CombinedRenderTests
{
    public sealed class CommentTimelineCursorTests
    {
        [Fact]
        public void CursorUsesOneTrimSearchThenAdvancesAcrossEqualOffsets()
        {
            var comments = new List<Comment>
            {
                CreateComment("a", 0.2),
                CreateComment("b", 0.2),
                CreateComment("c", 0.4),
                CreateComment("d", 1.0)
            };

            var cursor = new CommentTimelineCursor(comments, 0.25);

            Assert.Equal(1, cursor.LatestCommentIndex);
            Assert.Equal(1, cursor.AdvanceTo(0.39));
            Assert.Equal(2, cursor.AdvanceTo(0.4));
            Assert.Equal(3, cursor.AdvanceTo(2.0));
        }

        [Fact]
        public void CommentProcessingRestoresChronologicalOrderStably()
        {
            var comments = new List<Comment>
            {
                CreateComment("late", 1.0),
                CreateComment("first-equal", 0.4),
                CreateComment("second-equal", 0.4),
                CreateComment("early", 0.2)
            };
            var processor = new CommentProcessor(new ChatRenderOptions
            {
                UpdateRate = 0.2,
                DisperseCommentOffsets = false
            });

            processor.ProcessComments(comments);

            Assert.Equal(
                new[] { "early", "first-equal", "second-equal", "late" },
                comments.Select(x => x._id));
        }

        private static Comment CreateComment(string id, double offset)
        {
            return new Comment
            {
                _id = id,
                content_offset_seconds = offset,
                commenter = new Commenter
                {
                    name = "fixture",
                    display_name = "Fixture"
                },
                message = new Message
                {
                    body = id,
                    fragments = new List<Fragment> { new() { text = id } }
                }
            };
        }
    }
}
