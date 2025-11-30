using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.ChatRender.Processing
{
    /// <summary>
    /// Result of image fetching operations
    /// </summary>
    public struct FetchedImages
    {
        public List<ChatBadge> Badges;
        public List<TwitchEmote> Emotes;
        public List<TwitchEmote> ThirdPartyEmotes;
        public List<CheerEmote> Cheermotes;
        public Dictionary<string, SKBitmap> Emojis;
        public Dictionary<string, SKBitmap> Avatars;
    }

    /// <summary>
    /// Fetches and scales images needed for rendering
    /// </summary>
    public sealed class ImageFetcher
    {
        private readonly string _cacheDir;
        private readonly ChatRenderOptions _options;
        private readonly ITaskProgress _progress;
        private readonly string[] _defaultAvatarUrls;

        public ImageFetcher(string cacheDir, ChatRenderOptions options, ITaskProgress progress, string[] defaultAvatarUrls)
        {
            _cacheDir = cacheDir;
            _options = options;
            _progress = progress;
            _defaultAvatarUrls = defaultAvatarUrls;
        }

        public async Task<FetchedImages> FetchAllImagesAsync(ChatRoot chatRoot, CancellationToken cancellationToken)
        {
            var badgeTask = GetScaledBadges(chatRoot, cancellationToken);
            var emoteTask = GetScaledEmotes(chatRoot, cancellationToken);
            var emoteThirdTask = GetScaledThirdEmotes(chatRoot, cancellationToken);
            var cheerTask = GetScaledBits(chatRoot, cancellationToken);
            var emojiTask = GetScaledEmojis(cancellationToken);
            var avatarTask = _options.RenderUserAvatars
                ? GetScaledAvatars(chatRoot, cancellationToken)
        : Task.FromResult(new Dictionary<string, SKBitmap>());

            await Task.WhenAll(badgeTask, emoteTask, emoteThirdTask, cheerTask, emojiTask, avatarTask);

            return new FetchedImages
            {
                Badges = badgeTask.Result,
                Emotes = emoteTask.Result,
                ThirdPartyEmotes = emoteThirdTask.Result,
                Cheermotes = cheerTask.Result,
                Emojis = emojiTask.Result,
                Avatars = avatarTask.Result
            };
        }

        private async Task<List<ChatBadge>> GetScaledBadges(ChatRoot chatRoot, CancellationToken cancellationToken)
        {
            // Do not fetch if badges are disabled
            if (!_options.ChatBadges)
            {
                return new List<ChatBadge>();
            }

            var badgeTask = await TwitchHelper.GetChatBadges(
                chatRoot.comments,
                chatRoot.streamer.id,
              _cacheDir,
                   _progress,
              chatRoot.embeddedData,
              _options.Offline,
                cancellationToken);

            var newHeight = (int)Math.Round(36 * _options.ReferenceScale * _options.BadgeScale);
            var snapThreshold = (int)Math.Round(1 * _options.ReferenceScale);

            foreach (var badge in badgeTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                badge.SnapResize(newHeight, snapThreshold, snapThreshold);
            }

            return badgeTask;
        }

        private async Task<List<TwitchEmote>> GetScaledEmotes(ChatRoot chatRoot, CancellationToken cancellationToken)
        {
            var emoteTask = await TwitchHelper.GetEmotes(
        chatRoot.comments,
      _cacheDir,
     _progress,
           chatRoot.embeddedData,
       _options.Offline,
     cancellationToken);

            var snapThreshold = (int)Math.Round(4 * _options.ReferenceScale);

            foreach (var emote in emoteTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var newScale = 2.0 / emote.ImageScale * _options.ReferenceScale * _options.EmoteScale;
                emote.SnapScale(newScale, snapThreshold, snapThreshold);
            }

            return emoteTask;
        }

        private async Task<List<TwitchEmote>> GetScaledThirdEmotes(ChatRoot chatRoot, CancellationToken cancellationToken)
        {
            var emoteThirdTask = await TwitchHelper.GetThirdPartyEmotes(
            chatRoot.comments,
            chatRoot.streamer.id,
                _cacheDir,
             _progress,
               chatRoot.embeddedData,
          _options.BttvEmotes,
               _options.FfzEmotes,
                _options.StvEmotes,
        _options.AllowUnlistedEmotes,
               _options.Offline,
            cancellationToken);

            var snapThreshold = (int)Math.Round(4 * _options.ReferenceScale);

            foreach (var emote in emoteThirdTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var newScale = 2.0 / emote.ImageScale * _options.ReferenceScale * _options.EmoteScale;
                emote.SnapScale(newScale, snapThreshold, snapThreshold);
            }

            return emoteThirdTask;
        }

        private async Task<List<CheerEmote>> GetScaledBits(ChatRoot chatRoot, CancellationToken cancellationToken)
        {
            var cheerTask = await TwitchHelper.GetBits(
        chatRoot.comments,
  _cacheDir,
    chatRoot.streamer.id.ToString(),
     _progress,
                chatRoot.embeddedData,
   _options.Offline,
     cancellationToken);

            var snapThreshold = (int)Math.Round(4 * _options.ReferenceScale);

            foreach (var cheer in cheerTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var imageScale = cheer.tierList.FirstOrDefault().Value?.ImageScale ?? 2;
                var newScale = 2.0 / imageScale * _options.ReferenceScale * _options.EmoteScale;
                cheer.SnapScale(newScale, snapThreshold, snapThreshold);
            }

            return cheerTask;
        }

        private async Task<Dictionary<string, SKBitmap>> GetScaledEmojis(CancellationToken cancellationToken)
        {
            var emojis = await TwitchHelper.GetEmojis(_cacheDir, _options.EmojiVendor, _progress, cancellationToken);

            var newHeight = (int)Math.Round(36 * _options.ReferenceScale * _options.EmojiScale);

            // We can't just enumerate the dictionary because of the version checks
            string[] emojiKeys = emojis.Keys.ToArray();
            foreach (var emojiKey in emojiKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SKBitmap bitmap = emojis[emojiKey];
                SKImageInfo oldEmojiInfo = bitmap.Info;
                SKImageInfo imageInfo = new SKImageInfo((int)(newHeight / (double)oldEmojiInfo.Height * oldEmojiInfo.Width), newHeight);
                SKBitmap newBitmap = new SKBitmap(imageInfo);
                bitmap.ScalePixels(newBitmap, SKFilterQuality.High);
                bitmap.Dispose();
                newBitmap.SetImmutable();
                emojis[emojiKey] = newBitmap;
            }

            return emojis;
        }

        private async Task<Dictionary<string, SKBitmap>> GetScaledAvatars(ChatRoot chatRoot, CancellationToken cancellationToken)
        {
            var avatars = await TwitchHelper.GetAvatars(
             chatRoot.comments,
           _defaultAvatarUrls,
            _cacheDir,
           _progress,
         _options.Offline,
             cancellationToken);

            var newHeight = (int)Math.Round(36 * _options.ReferenceScale * _options.AvatarScale);

            using var maskPath = new SKPath();
            var radius = newHeight / 2;
            maskPath.AddCircle(radius, radius, radius);

            var avatarKeys = avatars.Keys.ToArray();
            foreach (var avatar in avatarKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var oldBitmap = avatars[avatar];
                var oldImageInfo = oldBitmap.Info;
                var imageInfo = new SKImageInfo((int)(newHeight / (double)oldImageInfo.Height * oldImageInfo.Width), newHeight);
                var newBitmap = new SKBitmap(imageInfo);
                oldBitmap.ScalePixels(newBitmap, SKFilterQuality.High);
                oldBitmap.Dispose();

                // Clip avatar to circle
                using (var canvas = new SKCanvas(newBitmap))
                {
                    canvas.ClipPath(maskPath, SKClipOperation.Difference, true);
                    canvas.Clear();
                }

                newBitmap.SetImmutable();
                avatars[avatar] = newBitmap;
            }

            return avatars;
        }
    }
}
