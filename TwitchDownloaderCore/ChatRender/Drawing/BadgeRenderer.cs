using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using SkiaSharp;
using TwitchDownloaderCore.ChatRender.Caching;
using TwitchDownloaderCore.ChatRender.Core;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.ChatRender.Drawing
{
    /// <summary>
    /// Renders chat badges for chat messages
    /// Mostly adapted from original TwitchDownloader project, with some defensive checks and optimizations
    /// </summary>
    public sealed class BadgeRenderer
    {
        private readonly ChatRenderOptions _options;
        private readonly RenderContext _context;
        private readonly ImageCache _imageCache;
        // Leaving this here in case of multiple badges per user in the future
        private readonly BitmapCache _bitmapCache;

        // Delegate for adding image sections (injected from SectionRenderer)
        private readonly Action<RenderContext.DrawingState, Point> _addImageSectionCallback;

        public BadgeRenderer(
            ChatRenderOptions options,
            RenderContext context,
            ImageCache imageCache,
            BitmapCache bitmapCache,
            Action<RenderContext.DrawingState, Point> addImageSectionCallback)
        {
            _options = options;
            _context = context;
            _imageCache = imageCache;
            _bitmapCache = bitmapCache;
            _addImageSectionCallback = addImageSectionCallback ?? throw new ArgumentNullException(nameof(addImageSectionCallback));
        }

        public void DrawBadges(Comment comment, ref RenderContext.DrawingState state)
        {
            // No badges to draw -> return early
            if (comment.message.user_badges == null || comment.message.user_badges.Count == 0)
            {
                return;
            }

            var badgeImages = ParseCommentBadges(comment);
            if (badgeImages.Count == 0)
            {
                return;
            }

            // Calculate total width needed for all badges
            int totalBadgeWidth = CalculateTotalBadgeWidth(badgeImages);

            // Check if we need to wrap to next section
            if (state.DrawPosition.X + totalBadgeWidth > _options.ChatWidth - _options.SidePadding * 2)
            {
                _addImageSectionCallback(state, state.DefaultPosition);
            }

            // Ensure we have a valid canvas for the current section bitmap
            if (state.CurrentCanvas == null && state.SectionImages.Count > 0)
            {
                var currentBitmap = state.SectionImages[state.SectionImages.Count - 1].bitmap;
                state.CurrentCanvas = _bitmapCache.GetOrCreateCanvas(currentBitmap);
            }

            // Draw each badge
            foreach (var (badgeImage, badgeType) in badgeImages)
            {
                // Skip filtered badges
                if (((ChatBadgeType)_options.ChatBadgeMask).HasFlag(badgeType))
                {
                    continue;
                }

                // Switched to use rendercontext's canvas directly for performance and consistency
                float badgeY = _context.SectionVerticalCenter - badgeImage.Height / 2f;
                state.CurrentCanvas.DrawBitmap(badgeImage, state.DrawPosition.X, badgeY);
                state.DrawPosition.X += badgeImage.Width + _options.WordSpacing / 2;
            }
        }

        /// <summary>
        /// Calculates the total width needed to render all badges, to protect against overflow.
        /// Calculates spacing as N-1 spaces for N badges.
        /// </summary>
        private int CalculateTotalBadgeWidth(List<(SKBitmap badgeImage, ChatBadgeType badgeType)> badges)
        {
            int totalWidth = 0;
            int validBadgeCount = 0;

            foreach (var (badgeImage, badgeType) in badges)
            {
                if (((ChatBadgeType)_options.ChatBadgeMask).HasFlag(badgeType))
                {
                    continue;
                }

                totalWidth += badgeImage.Width;
                validBadgeCount++;
            }

            // Add spacing between badges (but not after the last one)
            if (validBadgeCount > 0)
            {
                totalWidth += (validBadgeCount - 1) * (_options.WordSpacing / 2);
            }

            return totalWidth;
        }

        /// <summary>
        /// Parses and retrieves badge bitmaps for a comment
        /// </summary>
        private List<(SKBitmap badgeImage, ChatBadgeType badgeType)> ParseCommentBadges(Comment comment)
        {
            var badgeList = new List<(SKBitmap, ChatBadgeType)>();

            if (comment.message.user_badges == null)
            {
                return badgeList;
            }

            foreach (var badge in comment.message.user_badges)
            {
                if (!TryGetBadge(_imageCache.Badges, badge._id, out var cachedBadge))
                {
                    continue;
                }

                if (!cachedBadge.Versions.TryGetValue(badge.version, out var badgeBitmap))
                {
                    continue;
                }

                badgeList.Add((badgeBitmap, cachedBadge.Type));
            }

            return badgeList;
        }

        /// <summary>
        /// Binary search for chat badge by name
        /// </summary>
        private static bool TryGetBadge(
            List<ChatBadge> badgeList,
            ReadOnlySpan<char> badgeName,
            [NotNullWhen(true)] out ChatBadge badge)
        {
            var badgeSpan = CollectionsMarshal.AsSpan(badgeList);
            var lo = 0;
            var hi = badgeSpan.Length - 1;

            while (lo <= hi)
            {
                var i = lo + ((hi - lo) >> 1);
                var order = badgeSpan[i].Name.AsSpan().CompareTo(badgeName, StringComparison.Ordinal);

                if (order == 0)
                {
                    badge = badgeSpan[i];
                    return true;
                }

                if (order < 0)
                {
                    lo = i + 1;
                }
                else
                {
                    hi = i - 1;
                }
            }

            badge = null;
            return false;
        }
    }
}