using System.Runtime.CompilerServices;
using SkiaSharp;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Tests.CombinedRenderTests
{
    public sealed class AssetReferenceFilteringTests
    {
        [Fact]
        public void ReferencedAssetIndexesDeduplicateExactIdsAndVersions()
        {
            var comments = new List<Comment>
            {
                CreateComment("25", ("subscriber", "6"), ("moderator", "1")),
                CreateComment("25", ("subscriber", "12")),
                CreateComment("1902", ("subscriber", "6"))
            };

            var emotes = TwitchHelper.GetReferencedFirstPartyEmoteIds(comments);
            var badges = TwitchHelper.GetReferencedBadgeVersions(comments);

            Assert.Equal(new[] { "1902", "25" }, emotes.OrderBy(x => x));
            Assert.Equal(new[] { "moderator", "subscriber" }, badges.Keys.OrderBy(x => x));
            Assert.Equal(new[] { "12", "6" }, badges["subscriber"].OrderBy(x => x));
            Assert.Equal(new[] { "1" }, badges["moderator"]);
        }

        [Fact]
        public async Task OfflineEmbeddedAssetsDecodeOnlyReferencedIdsAndBadgeVersions()
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), $"twitchdownloader-assets-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            byte[] imageBytes = CreatePng();
            var comments = new List<Comment> { CreateComment("used-emote", ("subscriber", "6")) };
            var embedded = new EmbeddedData
            {
                firstParty = new List<EmbedEmoteData>
                {
                    CreateEmbeddedEmote("used-emote", imageBytes),
                    CreateEmbeddedEmote("unused-emote", imageBytes)
                },
                twitchBadges = new List<EmbedChatBadge>
                {
                    new()
                    {
                        name = "subscriber",
                        versions = new Dictionary<string, ChatBadgeData>
                        {
                            ["6"] = new() { bytes = imageBytes },
                            ["12"] = new() { bytes = imageBytes }
                        }
                    },
                    new()
                    {
                        name = "moderator",
                        versions = new Dictionary<string, ChatBadgeData>
                        {
                            ["1"] = new() { bytes = imageBytes }
                        }
                    }
                }
            };

            try
            {
                var emotes = await TwitchHelper.GetEmotes(
                    comments, tempDirectory, NullTaskLogger.Instance, embedded, offline: true);
                var badges = await TwitchHelper.GetChatBadges(
                    comments, 1, tempDirectory, NullTaskLogger.Instance, embedded, offline: true);

                try
                {
                    Assert.Collection(emotes, emote => Assert.Equal("used-emote", emote.Id));
                    var badge = Assert.Single(badges);
                    Assert.Equal("subscriber", badge.Name);
                    Assert.Equal(new[] { "6" }, badge.Versions.Keys);
                }
                finally
                {
                    foreach (var emote in emotes)
                        emote.Dispose();
                    foreach (var badge in badges)
                        badge.Dispose();
                }
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        private static Comment CreateComment(string emoteId, params (string id, string version)[] badges)
        {
            return new Comment
            {
                message = new Message
                {
                    body = "fixture",
                    fragments = new List<Fragment>
                    {
                        new() { text = "fixture", emoticon = new Emoticon { emoticon_id = emoteId } }
                    },
                    user_badges = badges
                        .Select(x => new UserBadge { _id = x.id, version = x.version })
                        .ToList()
                }
            };
        }

        private static EmbedEmoteData CreateEmbeddedEmote(string id, byte[] imageBytes)
        {
            return new EmbedEmoteData
            {
                id = id,
                name = id,
                imageScale = 2,
                data = imageBytes,
                width = 2,
                height = 2
            };
        }

        private static byte[] CreatePng()
        {
            using var bitmap = new SKBitmap(2, 2);
            bitmap.Erase(SKColors.Purple);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        private sealed class NullTaskLogger : ITaskLogger
        {
            public static NullTaskLogger Instance { get; } = new();

            public void LogVerbose(string logMessage) { }
            public void LogVerbose(DefaultInterpolatedStringHandler logMessage) { }
            public void LogInfo(string logMessage) { }
            public void LogInfo(DefaultInterpolatedStringHandler logMessage) { }
            public void LogWarning(string logMessage) { }
            public void LogWarning(DefaultInterpolatedStringHandler logMessage) { }
            public void LogError(string logMessage) { }
            public void LogError(DefaultInterpolatedStringHandler logMessage) { }
            public void LogFfmpeg(string logMessage) { }
        }
    }
}
