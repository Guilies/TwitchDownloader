using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Tests.Fixtures
{
    internal static class CombinedRenderFixtureFactory
    {
        internal static ChatRoot CreateChatRoot(double startSeconds, double endSeconds)
        {
            var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return new ChatRoot
            {
                streamer = new Streamer
                {
                    id = 1,
                    login = "fixture_streamer",
                    name = "Fixture Streamer"
                },
                video = new Video
                {
                    id = "1",
                    title = "Deterministic combined-render fixture",
                    description = "Generated locally; contains no downloaded assets.",
                    created_at = createdAt,
                    start = startSeconds,
                    end = endSeconds,
                    length = endSeconds - startSeconds,
                    game = "Fixture"
                },
                comments = new List<Comment>
                {
                    CreateComment("comment-1", createdAt, startSeconds + 0.125, "First fixture message"),
                    CreateComment("comment-2", createdAt.AddSeconds(1), startSeconds + 0.625, "Second fixture message"),
                    CreateComment("comment-3", createdAt.AddSeconds(2), endSeconds - 0.125, "Final fixture message")
                },
                embeddedData = new EmbeddedData()
            };
        }

        private static Comment CreateComment(
            string id,
            DateTime createdAt,
            double offsetSeconds,
            string body)
        {
            return new Comment
            {
                _id = id,
                created_at = createdAt,
                channel_id = "1",
                content_type = "video",
                content_id = "1",
                content_offset_seconds = offsetSeconds,
                commenter = new Commenter
                {
                    _id = "1",
                    name = "fixture_user",
                    display_name = "Fixture User",
                    created_at = createdAt,
                    updated_at = createdAt
                },
                message = new Message
                {
                    body = body,
                    bits_spent = 0,
                    fragments = new List<Fragment> { new() { text = body } },
                    user_badges = new List<UserBadge>(),
                    emoticons = new List<Emoticon2>(),
                    user_color = "#7B2CF2"
                }
            };
        }
    }
}
