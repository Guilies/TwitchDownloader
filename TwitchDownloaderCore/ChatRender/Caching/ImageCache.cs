using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.ChatRender.Caching
{
    /// <summary>
    /// Store and provide access to fetched images
    /// Note: potenially missing binary search lookup helpers (e.g. GetBadgeByName)
    /// </summary>
    public sealed class ImageCache : IDisposable
    {
        public List<ChatBadge> Badges { get; private set; }
        public List<TwitchEmote> Emotes { get; private set; }
        public List<TwitchEmote> ThirdPartyEmotes { get; private set; }
        public List<CheerEmote> Cheermotes { get; private set; }
        public Dictionary<string, SKBitmap> Emojis { get; private set; }
        public Dictionary<string, SKBitmap> Avatars { get; private set; }

        public ImageCache()
        {
            Badges = new List<ChatBadge>();
            Emotes = new List<TwitchEmote>();
            ThirdPartyEmotes = new List<TwitchEmote>();
            Cheermotes = new List<CheerEmote>();
            Emojis = new Dictionary<string, SKBitmap>();
            Avatars = new Dictionary<string, SKBitmap>();
        }

        public void Initialize(
             List<ChatBadge> badges,
                 List<TwitchEmote> emotes,
          List<TwitchEmote> thirdPartyEmotes,
         List<CheerEmote> cheermotes,
        Dictionary<string, SKBitmap> emojis,
            Dictionary<string, SKBitmap> avatars)
        {
            Badges = badges;
            Emotes = emotes;
            ThirdPartyEmotes = thirdPartyEmotes;
            Cheermotes = cheermotes;
            Emojis = emojis;
            Avatars = avatars;

            // Sort lists for binary search
            Badges.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            Emotes.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
            ThirdPartyEmotes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            Cheermotes.Sort((a, b) => string.Compare(a.prefix, b.prefix, StringComparison.Ordinal));
        }

        public void Dispose()
        {
            // Dispose badge bitmaps
            foreach (var badge in Badges)
            {
                badge?.Dispose();
            }
            Badges.Clear();

            // Dispose emote bitmaps
            foreach (var emote in Emotes)
            {
                emote?.Dispose();
            }
            Emotes.Clear();

            // Dispose third party emote bitmaps
            foreach (var emote in ThirdPartyEmotes)
            {
                emote?.Dispose();
            }
            ThirdPartyEmotes.Clear();

            // Dispose cheermote bitmaps
            foreach (var cheerEmote in Cheermotes)
            {
                cheerEmote?.Dispose();
            }
            Cheermotes.Clear();

            // Dispose emoji bitmaps
            foreach (var (_, bitmap) in Emojis)
            {
                bitmap?.Dispose();
            }
            Emojis.Clear();

            // Dispose avatar bitmaps
            foreach (var (_, bitmap) in Avatars)
            {
                bitmap?.Dispose();
            }
            Avatars.Clear();
        }
    }
}
