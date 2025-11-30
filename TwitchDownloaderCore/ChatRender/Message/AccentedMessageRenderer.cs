using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using TwitchDownloaderCore.ChatRender.Caching;
using TwitchDownloaderCore.ChatRender.Core;
using TwitchDownloaderCore.ChatRender.Drawing;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.ChatRender.Message
{
    /// <summary>
    /// Handles rendering of special accented message types (subscriptions, gifts, watch streaks, etc.)
    /// </summary>
    public sealed class AccentedMessageRenderer
    {
        private static readonly SKColor Purple = SKColor.Parse("#7B2CF2");

        private readonly ChatRenderOptions _options;
        private readonly RenderContext _context;
        private readonly BitmapCache _bitmapCache;
        private readonly FontCache _fontCache;
        private readonly HighlightIcons _highlightIcons;
        private readonly MessageRenderer _messageRenderer;
        private readonly TextRenderer _textRenderer;

        // Delegate for adding image sections (injected from SectionRenderer)
        private readonly Action<RenderContext.DrawingState, Point> _addImageSectionCallback;

        // Delegate for DrawNonAccentedMessage (injected from SectionRenderer)
        private readonly DrawNonAccentedMessageDelegate _drawNonAccentedMessageCallback;

        public delegate void DrawNonAccentedMessageDelegate(
            Comment comment,
            ref RenderContext.DrawingState state,
            List<(Point, TwitchEmote)> emotePositionList,
            bool highlightWords,
            int commentIndex);

        public AccentedMessageRenderer(
            ChatRenderOptions options,
            RenderContext context,
            BitmapCache bitmapCache,
            FontCache fontCache,
            HighlightIcons highlightIcons,
            MessageRenderer messageRenderer,
            TextRenderer textRenderer,
            Action<RenderContext.DrawingState, Point> addImageSectionCallback,
            DrawNonAccentedMessageDelegate drawNonAccentedMessageCallback)
        {
            _options = options;
            _context = context;
            _bitmapCache = bitmapCache;
            _fontCache = fontCache;
            _highlightIcons = highlightIcons;
            _messageRenderer = messageRenderer;
            _textRenderer = textRenderer;
            _addImageSectionCallback = addImageSectionCallback ?? throw new ArgumentNullException(nameof(addImageSectionCallback));
            _drawNonAccentedMessageCallback = drawNonAccentedMessageCallback ?? throw new ArgumentNullException(nameof(drawNonAccentedMessageCallback));
        }

        /// <summary>
        /// Ensures state.CurrentCanvas is initialized before drawing operations
        /// </summary>
        private void EnsureCanvasInitialized(ref RenderContext.DrawingState state)
        {
            if (state.CurrentCanvas == null && state.SectionImages.Count > 0)
            {
                var currentBitmap = state.SectionImages[state.SectionImages.Count - 1].bitmap;
                state.CurrentCanvas = _bitmapCache.GetOrCreateCanvas(currentBitmap);
            }
        }

        public void DrawAccentedMessage(
            Comment comment,
            ref RenderContext.DrawingState state,
            List<(Point, TwitchEmote)> emotePositionList,
            HighlightType highlightType,
            int commentIndex)
        {
            state.DrawPosition.X += _options.AccentIndentWidth;
            state.DefaultPosition.X = state.DrawPosition.X;

            var highlightIcon = _highlightIcons.GetHighlightIcon(highlightType, _fontCache.MessageFont.Color);

            Point iconPoint = new()
            {
                X = state.DrawPosition.X,
                Y = (int)((_options.SectionHeight - highlightIcon.Height) / 2.0)
            };

            switch (highlightType)
            {
                case HighlightType.SubscribedTier:
                case HighlightType.SubscribedPrime:
                    DrawSubscribeMessage(comment, ref state, emotePositionList, commentIndex, highlightIcon, iconPoint);
                    break;
                case HighlightType.BitBadgeTierNotification:
                    DrawBitsBadgeTierMessage(comment, ref state, emotePositionList, highlightIcon, iconPoint);
                    break;
                case HighlightType.WatchStreak:
                    DrawWatchStreakMessage(comment, ref state, emotePositionList, commentIndex, highlightIcon, iconPoint);
                    break;
                case HighlightType.CharityDonation:
                    DrawCharityDonationMessage(comment, ref state, emotePositionList, highlightIcon, iconPoint);
                    break;
                case HighlightType.GiftedMany:
                case HighlightType.GiftedSingle:
                case HighlightType.GiftedAnonymous:
                case HighlightType.ContinuingAnonymousGift:
                    DrawGiftMessage(comment, ref state, emotePositionList, highlightIcon, iconPoint);
                    break;
                case HighlightType.ChannelPointHighlight:
                    _drawNonAccentedMessageCallback(comment, ref state, emotePositionList, true, commentIndex);
                    break;
                case HighlightType.ContinuingGift:
                case HighlightType.PayingForward:
                case HighlightType.Raid:
                case HighlightType.Combo:
                default:
                    _messageRenderer.DrawMessage(comment, ref state, emotePositionList, false);
                    break;
            }
        }

        private void DrawSubscribeMessage(
            Comment comment,
            ref RenderContext.DrawingState state,
            List<(Point, TwitchEmote)> emoteSectionList,
            int commentIndex,
            SKImage highlightIcon,
            Point iconPoint)
        {
            EnsureCanvasInitialized(ref state);
            state.CurrentCanvas.DrawImage(highlightIcon, iconPoint.X, iconPoint.Y);

            Point customMessagePos = state.DrawPosition;
            state.DrawPosition.X += highlightIcon.Width + _options.WordSpacing;
            state.DefaultPosition.X = state.DrawPosition.X;

            _textRenderer.DrawUsername(comment, ref state, false, Purple, commentIndex);
            _addImageSectionCallback(state, state.DefaultPosition);

            // Remove the commenter's name from the resub message
            comment.message.body = comment.message.body[(comment.commenter.display_name.Length + 1)..];
            if (comment.message.fragments[0].text.Equals(comment.commenter.display_name, StringComparison.OrdinalIgnoreCase))
            {
                // Some older chat replays separate user names into separate fragments
                comment.message.fragments.RemoveAt(0);
            }
            else
            {
                comment.message.fragments[0].text = comment.message.fragments[0].text[(comment.commenter.display_name.Length + 1)..];
            }

            var (resubMessage, customResubMessage) = HighlightIcons.SplitSubComment(comment);
            _messageRenderer.DrawMessage(resubMessage, ref state, emoteSectionList, false);

            // Return if there is no custom resub message to draw
            if (customResubMessage is null)
            {
                return;
            }

            _addImageSectionCallback(state, state.DefaultPosition);
            state.DrawPosition = customMessagePos;
            state.DefaultPosition = customMessagePos;
            _drawNonAccentedMessageCallback(customResubMessage, ref state, emoteSectionList, false, commentIndex);
        }

        private void DrawBitsBadgeTierMessage(
            Comment comment,
            ref RenderContext.DrawingState state,
            List<(Point, TwitchEmote)> emoteSectionList,
            SKImage highlightIcon,
            Point iconPoint)
        {
            EnsureCanvasInitialized(ref state);
            state.CurrentCanvas.DrawImage(highlightIcon, iconPoint.X, iconPoint.Y);
            state.DrawPosition.X += highlightIcon.Width + _options.WordSpacing;
            state.DefaultPosition.X = state.DrawPosition.X;

            if (comment.message.fragments.Count == 1)
            {
                _textRenderer.DrawUsername(comment, ref state, false, _fontCache.MessageFont.Color);

                var bitsBadgeVersion = comment.message.user_badges.FirstOrDefault(x => x._id == "bits")?.version;
                if (bitsBadgeVersion is not null)
                {
                    comment.message.body = bitsBadgeVersion.Length > 3
                        ? $"just earned a new {bitsBadgeVersion.AsSpan(0, bitsBadgeVersion.Length - 3)}K Bits badge!"
                        : $"just earned a new {bitsBadgeVersion} Bits badge!";
                }
                else
                {
                    comment.message.body = "just earned a new Bits badge!";
                }

                comment.message.fragments[0].text = comment.message.body;
            }
            else
            {
                // This should never be possible, but just in case.
                _textRenderer.DrawUsername(comment, ref state, true, _fontCache.MessageFont.Color);
            }

            _messageRenderer.DrawMessage(comment, ref state, emoteSectionList, false);
        }

        private void DrawWatchStreakMessage(
            Comment comment,
            ref RenderContext.DrawingState state,
            List<(Point, TwitchEmote)> emoteSectionList,
            int commentIndex,
            SKImage highlightIcon,
            Point iconPoint)
        {
            EnsureCanvasInitialized(ref state);
            state.CurrentCanvas.DrawImage(highlightIcon, iconPoint.X, iconPoint.Y);

            Point customMessagePos = state.DrawPosition;
            state.DrawPosition.X += highlightIcon.Width + _options.WordSpacing;
            state.DefaultPosition.X = state.DrawPosition.X;

            _textRenderer.DrawUsername(comment, ref state, false, Purple);
            _addImageSectionCallback(state, state.DefaultPosition);

            // Remove the commenter's name from the watch streak message
            comment.message.body = comment.message.body[(comment.commenter.display_name.Length + 1)..];
            if (comment.message.fragments[0].text.Equals(comment.commenter.display_name, StringComparison.OrdinalIgnoreCase))
            {
                // This is necessary for sub messages. We'll keep it around just in case.
                comment.message.fragments.RemoveAt(0);
            }
            else
            {
                comment.message.fragments[0].text = comment.message.fragments[0].text[(comment.commenter.display_name.Length + 1)..];
            }

            var (streakMessage, customMessage) = HighlightIcons.SplitWatchStreakComment(comment);
            _messageRenderer.DrawMessage(streakMessage, ref state, emoteSectionList, false);

            // Return if there is no custom message to draw
            if (customMessage is null)
            {
                return;
            }

            _addImageSectionCallback(state, state.DefaultPosition);
            state.DrawPosition = customMessagePos;
            state.DefaultPosition = customMessagePos;
            _drawNonAccentedMessageCallback(customMessage, ref state, emoteSectionList, false, commentIndex);
        }

        private void DrawCharityDonationMessage(
            Comment comment,
            ref RenderContext.DrawingState state,
            List<(Point, TwitchEmote)> emoteSectionList,
            SKImage highlightIcon,
            Point iconPoint)
        {
            EnsureCanvasInitialized(ref state);
            state.CurrentCanvas.DrawImage(highlightIcon, iconPoint.X, iconPoint.Y);

            state.DrawPosition.X += highlightIcon.Width + _options.WordSpacing;
            state.DefaultPosition.X = state.DrawPosition.X;

            _textRenderer.DrawUsername(comment, ref state, false, Purple);
            _addImageSectionCallback(state, state.DefaultPosition);

            // Remove the commenter's name from the charity donation message
            comment.message.body = comment.message.body[(comment.commenter.display_name.Length + 2)..];
            comment.message.fragments[0].text = comment.message.fragments[0].text[(comment.commenter.display_name.Length + 2)..];

            _messageRenderer.DrawMessage(comment, ref state, emoteSectionList, false);
        }

        private void DrawGiftMessage(
            Comment comment,
            ref RenderContext.DrawingState state,
            List<(Point, TwitchEmote)> emoteSectionList,
            SKImage highlightIcon,
            Point iconPoint)
        {
            EnsureCanvasInitialized(ref state);
            state.CurrentCanvas.DrawImage(highlightIcon, iconPoint.X, iconPoint.Y);
            state.DrawPosition.X += highlightIcon.Width + _options.AccentIndentWidth - _options.AccentStrokeWidth;
            state.DefaultPosition.X = state.DrawPosition.X;
            _messageRenderer.DrawMessage(comment, ref state, emoteSectionList, false);
        }
    }
}
