using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using SkiaSharp;
using TwitchDownloaderCore.ChatRender.Caching;
using TwitchDownloaderCore.ChatRender.Core;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.ChatRender.Drawing
{
    /// <summary>
    /// Renders emotes (first-party and third-party) and handles animated emote frames
    /// </summary>
    public sealed class EmoteRenderer : IDisposable
    {
        private static readonly SKColor Purple = SKColor.Parse("#7B2CF2");

        private readonly ChatRenderOptions _options;
        private readonly RenderContext _context;
        private readonly ImageCache _imageCache;
        private readonly BitmapCache _bitmapCache;

        private SKBitmap _animatedFrameBuffer = null;
        private readonly object _animatedFrameLock = new();

        // Delegate for adding image sections (injected from SectionRenderer)
        private readonly Action<RenderContext.DrawingState, Point> _addImageSectionCallback;
        private readonly Func<RenderContext.DrawingState, int, bool> _checkAndWrapCallback;
        private readonly Action<RenderContext.DrawingState> _ensureCanvasCallback;

        public EmoteRenderer(
            ChatRenderOptions options,
            RenderContext context,
            ImageCache imageCache,
            BitmapCache bitmapCache,
            Action<RenderContext.DrawingState, Point> addImageSectionCallback,
            Func<RenderContext.DrawingState, int, bool> checkAndWrapCallback,
            Action<RenderContext.DrawingState> ensureCanvasCallback)
        {
            _options = options;
            _context = context;
            _imageCache = imageCache;
            _bitmapCache = bitmapCache;
            _addImageSectionCallback = addImageSectionCallback ?? throw new ArgumentNullException(nameof(addImageSectionCallback));
            _checkAndWrapCallback = checkAndWrapCallback ?? throw new ArgumentNullException(nameof(checkAndWrapCallback));
            _ensureCanvasCallback = ensureCanvasCallback ?? throw new ArgumentNullException(nameof(ensureCanvasCallback));
        }

        public void DrawFirstPartyEmote(
            Fragment fragment,
            ref RenderContext.DrawingState state,
            List<(Point, TwitchEmote)> emotePositionList,
            bool highlightWords)
        {
            if (!TryGetTwitchEmote(_imageCache.Emotes, fragment.emoticon.emoticon_id, out var emote))
            {
                // Emote not found (probably removed) - caller should handle this with text fallback
                return;
            }

            DrawEmoteCommon(emote, ref state, emotePositionList, highlightWords);
        }

        public void DrawThirdPartyEmote(
            TwitchEmote twitchEmote,
            ref RenderContext.DrawingState state,
            List<(Point, TwitchEmote)> emotePositionList,
            bool highlightWords)
        {
            DrawEmoteCommon(twitchEmote, ref state, emotePositionList, highlightWords);
        }

        /// <summary>
        /// Common emote drawing logic for both first-party and third-party emotes
        /// </summary>
        private void DrawEmoteCommon(
            TwitchEmote emote,
            ref RenderContext.DrawingState state,
            List<(Point, TwitchEmote)> emotePositionList,
            bool highlightWords)
        {
            var emoteInfo = emote.Info;
            Point emotePoint;

            if (!emote.IsZeroWidth)
            {
                // Check wrap before drawing emote
                int emoteWidth = emoteInfo.Width + _options.EmoteSpacing;
                _checkAndWrapCallback(state, emoteWidth);

                // Ensure we have a valid canvas for the current section bitmap
                _ensureCanvasCallback(state);

                // Draw highlight background if needed
                if (highlightWords)
                {
                    using var paint = new SKPaint { Color = Purple };
                    state.CurrentCanvas.DrawRect(
                        state.DrawPosition.X,
                        0,
                        emoteInfo.Width + _options.EmoteSpacing,
                        _options.SectionHeight,
                        paint);
                }

                emotePoint = new Point
                {
                    X = state.DrawPosition.X,
                    Y = CalculateEmoteVerticalPosition(state, emoteInfo.Height)
                };

                state.DrawPosition.X += emoteInfo.Width + _options.EmoteSpacing;
            }
            else
            {
                // Zero-width emote - overlay on previous position
                emotePoint = new Point
                {
                    X = state.DrawPosition.X - _options.EmoteSpacing - emoteInfo.Width,
                    Y = CalculateEmoteVerticalPosition(state, emoteInfo.Height)
                };
            }

            emotePositionList.Add((emotePoint, emote));
        }

        /// <summary>
        /// Calculates the vertical position to center an emote in the current section
        /// </summary>
        private int CalculateEmoteVerticalPosition(RenderContext.DrawingState state, int emoteHeight)
        {
            // Emote positions are relative to the current section (0 to SectionHeight)
            // Center the emote vertically within the section
            return (int)((_options.SectionHeight - emoteHeight) / 2.0);
        }

        public (SKBitmap frame, bool isCopyFrame) DrawAnimatedEmotes(
            SKBitmap updateFrame,
            List<CommentSection> comments,
            int currentTick)
        {
            // When generating a mask we must not mutate or reuse the underlying bitmap because SetFrameMask will alter pixel data.
            if (_options.GenerateMask)
            {
                return DrawAnimatedEmotesForMask(updateFrame, comments, currentTick);
            }

            // Check if any animated emotes exist
            if (!HasAnimatedEmotes(comments))
            {
                return (updateFrame, false);
            }

            // Reuse a preallocated frame buffer for animated overlay compositing
            lock (_animatedFrameLock)
            {
                EnsureFrameBufferSize(updateFrame.Info);

                var canvas = _bitmapCache.GetOrCreateCanvas(_animatedFrameBuffer);
                _animatedFrameBuffer.Erase(SKColors.Transparent);

                // Draw base (immutable) frame
                canvas.DrawBitmap(updateFrame, 0, 0);

                // Overlay animated emote frames
                DrawAnimatedEmoteLayers(canvas, comments, currentTick);

                canvas.Flush();
                return (_animatedFrameBuffer, false);
            }
        }

        /// <summary>
        /// Draws animated emotes for mask generation (requires bitmap copy)
        /// </summary>
        private (SKBitmap frame, bool isCopyFrame) DrawAnimatedEmotesForMask(
            SKBitmap updateFrame,
            List<CommentSection> comments,
            int currentTick)
        {
            SKBitmap maskedFrame = updateFrame.Copy();
            var frameCanvas = _bitmapCache.GetOrCreateCanvas(maskedFrame);

            DrawAnimatedEmoteLayers(frameCanvas, comments, currentTick);

            frameCanvas.Flush();
            return (maskedFrame, true);
        }

        /// <summary>
        /// Draws the animated emote layers onto the canvas
        /// </summary>
        private void DrawAnimatedEmoteLayers(SKCanvas canvas, List<CommentSection> comments, int currentTick)
        {
            int frameHeight = _options.ChatHeight;
            long currentTickMs = (long)(currentTick / (double)_options.Framerate * 1000);

            for (int c = comments.Count - 1; c >= 0; c--)
            {
                var comment = comments[c];
                frameHeight -= comment.Image.Height + _options.VerticalPadding;

                foreach (var (drawPoint, emote) in comment.Emotes)
                {
                    if (emote.FrameCount > 1)
                    {
                        int frameIndex = GetAnimatedEmoteFrameIndex(emote, currentTickMs);
                        canvas.DrawBitmap(
                            emote.EmoteFrames[frameIndex],
                            drawPoint.X,
                            drawPoint.Y + frameHeight);
                    }
                }
            }
        }

        /// <summary>
        /// Calculates which frame of an animated emote should be displayed at the given time
        /// </summary>
        private static int GetAnimatedEmoteFrameIndex(TwitchEmote emote, long currentTickMs)
        {
            long imageFrame = currentTickMs % (emote.TotalDuration * 10);

            for (int i = 0; i < emote.EmoteFrameDurations.Count; i++)
            {
                if (imageFrame - emote.EmoteFrameDurations[i] * 10 <= 0)
                {
                    return i;
                }
                imageFrame -= emote.EmoteFrameDurations[i] * 10;
            }

            return emote.EmoteFrameDurations.Count - 1;
        }

        /// <summary>
        /// Checks if any comments contain animated emotes
        /// </summary>
        private static bool HasAnimatedEmotes(List<CommentSection> comments)
        {
            foreach (var comment in comments)
            {
                foreach (var (_, emote) in comment.Emotes)
                {
                    if (emote.FrameCount > 1)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Ensures the animated frame buffer matches the required dimensions
        /// </summary>
        private void EnsureFrameBufferSize(SKImageInfo requiredInfo)
        {
            if (_animatedFrameBuffer == null ||
                _animatedFrameBuffer.Width != requiredInfo.Width ||
                _animatedFrameBuffer.Height != requiredInfo.Height)
            {
                _animatedFrameBuffer?.Dispose();
                _animatedFrameBuffer = new SKBitmap(requiredInfo);
            }
        }

        /// <summary>
        /// Binary search for Twitch emote by ID
        /// </summary>
        private static bool TryGetTwitchEmote(
            List<TwitchEmote> twitchEmoteList,
            ReadOnlySpan<char> emoteId,
            [NotNullWhen(true)] out TwitchEmote twitchEmote)
        {
            var emoteListSpan = CollectionsMarshal.AsSpan(twitchEmoteList);
            var lo = 0;
            var hi = emoteListSpan.Length - 1;

            while (lo <= hi)
            {
                var i = lo + ((hi - lo) >> 1);
                var order = emoteListSpan[i].Id.AsSpan().CompareTo(emoteId, StringComparison.Ordinal);

                if (order == 0)
                {
                    twitchEmote = emoteListSpan[i];
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

            twitchEmote = null;
            return false;
        }

        public void Dispose()
        {
            _animatedFrameBuffer?.Dispose();
            _animatedFrameBuffer = null;
        }
    }
}