using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using TwitchDownloaderCore.ChatRender.Caching;
using TwitchDownloaderCore.ChatRender.Core;
using TwitchDownloaderCore.ChatRender.Message;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.ChatRender.Drawing
{
    /// <summary>
    /// Top-level renderer that orchestrates the entire rendering pipeline
    /// </summary>
    public sealed class SectionRenderer
    {
        private static readonly SKColor Purple = SKColor.Parse("#7B2CF2");

        private readonly ChatRenderOptions _options;
        private readonly RenderContext _context;
        private readonly BitmapCache _bitmapCache;
        private readonly ImageCache _imageCache;
        private readonly FontCache _fontCache;
        private TimestampRenderer _timestampRenderer;
        private AvatarRenderer _avatarRenderer;
        private BadgeRenderer _badgeRenderer;
        private TextRenderer _textRenderer;
        private MessageRenderer _messageRenderer;
        private AccentedMessageRenderer _accentedMessageRenderer;
        private EmoteRenderer _emoteRenderer;
        private readonly HighlightIcons _highlightIcons;
        private readonly ITaskProgress _progress;

        // Chat root data
        private ChatRoot _chatRoot;

        // Pre-calculated update frame interval
        private readonly int _updateFrame;

        public SectionRenderer(
            ChatRenderOptions options,
            RenderContext context,
            BitmapCache bitmapCache,
            ImageCache imageCache,
            FontCache fontCache,
            TimestampRenderer timestampRenderer,
            AvatarRenderer avatarRenderer,
            BadgeRenderer badgeRenderer,
            TextRenderer textRenderer,
            MessageRenderer messageRenderer,
            EmoteRenderer emoteRenderer,
            HighlightIcons highlightIcons,
            ITaskProgress progress)
        {
            _options = options;
            _context = context;
            _bitmapCache = bitmapCache;
            _imageCache = imageCache;
            _fontCache = fontCache;
            _timestampRenderer = timestampRenderer;
            _avatarRenderer = avatarRenderer;
            _badgeRenderer = badgeRenderer;
            _textRenderer = textRenderer;
            _messageRenderer = messageRenderer;
            _emoteRenderer = emoteRenderer;
            _highlightIcons = highlightIcons;
            _progress = progress;

            // Calculate update frame interval
            _updateFrame = (int)(options.Framerate / options.UpdateRate);

            // Create AccentedMessageRenderer with proper delegates (if messageRenderer is provided)
            if (messageRenderer != null && textRenderer != null)
            {
                _accentedMessageRenderer = new AccentedMessageRenderer(
                    options,
                    context,
                    bitmapCache,
                    fontCache,
                    highlightIcons,
                    messageRenderer,
                    textRenderer,
                    (state, defaultPos) => this.AddImageSection(ref state, defaultPos),
                    DrawNonAccentedMessage
                );
            }
        }

        /// <summary>
        /// Sets the renderer dependencies after construction
        /// </summary>
        public void SetRenderers(
            TimestampRenderer timestampRenderer,
            AvatarRenderer avatarRenderer,
            BadgeRenderer badgeRenderer,
            TextRenderer textRenderer,
            MessageRenderer messageRenderer,
            EmoteRenderer emoteRenderer)
        {
            _timestampRenderer = timestampRenderer;
            _avatarRenderer = avatarRenderer;
            _badgeRenderer = badgeRenderer;
            _textRenderer = textRenderer;
            _messageRenderer = messageRenderer;
            _emoteRenderer = emoteRenderer;

            // Create AccentedMessageRenderer now that we have all renderers
            _accentedMessageRenderer = new AccentedMessageRenderer(
                _options,
                _context,
                _bitmapCache,
                _fontCache,
                _highlightIcons,
                messageRenderer,
                textRenderer,
                (state, defaultPos) => this.AddImageSection(ref state, defaultPos),
                DrawNonAccentedMessage
            );
        }

        public void SetChatRoot(ChatRoot chatRoot)
        {
            _chatRoot = chatRoot;
        }

        public void RenderSection(
            int startTick,
            int endTick,
            FfmpegProcess ffmpegProcess,
            FfmpegProcess maskProcess,
            CancellationToken cancellationToken)
        {
            UpdateFrame latestUpdate = null;
            var ffmpegStream = new BinaryWriter(ffmpegProcess.StandardInput.BaseStream);
            BinaryWriter maskStream = null;
            if (maskProcess != null)
                maskStream = new BinaryWriter(maskProcess.StandardInput.BaseStream);

            DriveInfo outputDrive = DriveHelper.GetOutputDrive(ffmpegProcess.SavePath);
            Stopwatch stopwatch = Stopwatch.StartNew();

            // Calculate section baseline Y position
            int sectionDefaultYPos = _context.SectionBaselineY;

            for (int currentTick = startTick; currentTick < endTick; currentTick++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (currentTick % _updateFrame == 0)
                {
                    latestUpdate = GenerateUpdateFrame(currentTick, sectionDefaultYPos, latestUpdate);
                }

                SKBitmap frame = null;
                bool isCopyFrame = false;
                try
                {
                    (frame, isCopyFrame) = GetFrameFromTick(currentTick, sectionDefaultYPos, latestUpdate);

                    if (!_options.SkipDriveWaiting)
                        DriveHelper.WaitForDrive(outputDrive, _progress, cancellationToken).Wait(cancellationToken);

                    ffmpegStream.Write(frame.Bytes);

                    if (maskProcess != null)
                    {
                        if (!_options.SkipDriveWaiting)
                            DriveHelper.WaitForDrive(outputDrive, _progress, cancellationToken).Wait(cancellationToken);

                        SetFrameMask(frame);
                        maskStream.Write(frame.Bytes);
                    }
                }
                finally
                {
                    if (isCopyFrame)
                    {
                        frame?.Dispose();
                    }
                }

                if (currentTick % 3 == 0)
                {
                    var percent = (currentTick - startTick) / (double)(endTick - startTick) * 100;
                    var elapsed = stopwatch.Elapsed;
                    var elapsedSeconds = elapsed.TotalSeconds;

                    var secondsLeft = unchecked((int)(100 / percent * elapsedSeconds - elapsedSeconds));
                    _progress.ReportProgress((int)Math.Round(percent), elapsed, TimeSpan.FromSeconds(secondsLeft));
                }
            }

            stopwatch.Stop();
            _progress.ReportProgress(100, stopwatch.Elapsed, TimeSpan.Zero);
            _progress.LogInfo($"FINISHED. RENDER TIME: {stopwatch.Elapsed.TotalSeconds:F1}s SPEED: {(endTick - startTick) / (double)_options.Framerate / stopwatch.Elapsed.TotalSeconds:F2}x");

            latestUpdate?.Image.Dispose();

            ffmpegStream.Dispose();
            maskStream?.Dispose();

            ffmpegProcess.WaitForExit(100_000);
            maskProcess?.WaitForExit(100_000);
        }

        private UpdateFrame GenerateUpdateFrame(int currentTick, int sectionDefaultYPos, UpdateFrame lastUpdate = null)
        {
            SKBitmap newFrame = new SKBitmap(_options.ChatWidth, _options.ChatHeight);
            double currentTimeSeconds = currentTick / (double)_options.Framerate;
            int newestCommentIndex = _chatRoot.comments.FindLastIndex(x => x.content_offset_seconds <= currentTimeSeconds);

            if (newestCommentIndex == lastUpdate?.CommentIndex)
            {
                return lastUpdate;
            }
            lastUpdate?.Image.Dispose();

            List<CommentSection> commentList = lastUpdate?.Comments ?? new List<CommentSection>();

            int oldCommentIndex = -1;
            if (commentList.Count > 0)
            {
                oldCommentIndex = commentList.Last().CommentIndex;
            }
            else if (newestCommentIndex > 100)
            {
                // If we are starting partially through the comment list, we don't want to needlessly render *every* comment before our starting comment.
                // Skipping to 100 comments before our starting index should be more than enough to fill the frame with previous comments
                oldCommentIndex = newestCommentIndex - 100;
            }

            if (newestCommentIndex > oldCommentIndex)
            {
                int currentIndex = oldCommentIndex + 1;

                do
                {
                    CommentSection comment = GenerateCommentSection(currentIndex, sectionDefaultYPos);
                    if (comment != null)
                    {
                        commentList.Add(comment);
                    }
                    currentIndex++;
                }
                while (newestCommentIndex >= currentIndex);
            }

            var frameCanvas = _bitmapCache.GetOrCreateCanvas(newFrame);
            int commentsDrawn = 0;
            int commentListIndex = commentList.Count - 1;
            int frameHeight = _options.ChatHeight;
            frameCanvas.Clear(_options.BackgroundColor);

            while (commentListIndex >= 0 && frameHeight > -_options.VerticalPadding)
            {
                var comment = commentList[commentListIndex];
                frameHeight -= comment.Image.Height + _options.VerticalPadding;

                if (_options.AlternateMessageBackgrounds && comment.CommentIndex % 2 == 1)
                {
                    frameCanvas.DrawRect(0, frameHeight - _options.VerticalPadding / 2f, newFrame.Width, comment.Image.Height + _options.VerticalPadding, _options.AlternateBackgroundPaint);
                }

                frameCanvas.DrawBitmap(comment.Image, 0, frameHeight);

                foreach (var (drawPoint, emote) in comment.Emotes)
                {
                    // Only draw static emotes
                    if (emote.FrameCount == 1)
                    {
                        frameCanvas.DrawBitmap(emote.EmoteFrames[0], drawPoint.X, drawPoint.Y + frameHeight);
                    }
                }
                commentsDrawn++;
                commentListIndex--;
            }

            int removeCount = commentList.Count - commentsDrawn;
            for (int i = 0; i < removeCount; i++)
            {
                commentList[i].Image.Dispose();
            }
            commentList.RemoveRange(0, removeCount);
            frameCanvas.Flush();

            return new UpdateFrame() { Image = newFrame, Comments = commentList, CommentIndex = newestCommentIndex };
        }

        private CommentSection GenerateCommentSection(int commentIndex, int sectionDefaultYPos)
        {
            CommentSection newSection = new CommentSection();
            List<(Point, TwitchEmote)> emoteSectionList = new List<(Point, TwitchEmote)>();
            Comment comment = _chatRoot.comments[commentIndex];

            var state = new RenderContext.DrawingState
            {
                SectionImages = new List<(SKImageInfo info, SKBitmap bitmap)>(),
                DrawPosition = new Point(),
                DefaultPosition = new Point { X = _options.SidePadding }
            };

            var highlightType = HighlightType.Unknown;

            if (comment.message.user_notice_params?.msg_id != null)
            {
                if (comment.message.user_notice_params.msg_id is not "highlighted-message" and not "sub" and not "resub" and not "subgift" and not "")
                {
                    _progress.LogVerbose($"{comment._id} has invalid {nameof(comment.message.user_notice_params)}: {comment.message.user_notice_params.msg_id}.");
                    return null;
                }

                if (comment.message.user_notice_params.msg_id == "highlighted-message")
                {
                    if (comment.message.fragments == null && comment.message.body != null)
                    {
                        comment.message.fragments = new List<Fragment> { new() { text = comment.message.body } };
                    }

                    highlightType = HighlightType.ChannelPointHighlight;
                }
            }

            if (comment.message.fragments == null || comment.commenter == null)
            {
                _progress.LogVerbose($"{comment._id} lacks fragments and/or a commenter.");
                return null;
            }

            AddImageSection(ref state, state.DefaultPosition);
            state.DefaultPosition.Y = sectionDefaultYPos;
            state.DrawPosition.Y = state.DefaultPosition.Y;
            
            // Initialize layout state
            state.LineStartX = state.DefaultPosition.X;
            state.MaxWidth = _options.ChatWidth - _options.SidePadding;
            state.CurrentLineHeight = 0;

            if (highlightType is HighlightType.Unknown)
            {
                highlightType = HighlightIcons.GetHighlightType(comment);
            }

            if (highlightType is not HighlightType.None)
            {
                if (highlightType is not HighlightType.ChannelPointHighlight && !_options.SubMessages)
                {
                    return null;
                }

                _accentedMessageRenderer.DrawAccentedMessage(comment, ref state, emoteSectionList, highlightType, commentIndex);
            }
            else
            {
                DrawNonAccentedMessage(comment, ref state, emoteSectionList, false, commentIndex);
            }

            SKBitmap finalBitmap = CombineImages(state.SectionImages, highlightType, commentIndex);
            newSection.Image = finalBitmap;
            newSection.Emotes = emoteSectionList;
            newSection.CommentIndex = commentIndex;

            return newSection;
        }

        /// <summary>
        /// Renders a non-accented (standard) chat message with all components
        /// </summary>
        public void DrawNonAccentedMessage(
            Comment comment,
            ref RenderContext.DrawingState state,
            List<(Point, TwitchEmote)> emotePositionList,
            bool highlightWords,
            int commentIndex)
        {
            if (_options.Timestamp)
            {
                _timestampRenderer.DrawTimestamp(comment, ref state);
            }
            if (_options.RenderUserAvatars)
            {
                _avatarRenderer.DrawAvatar(comment, ref state);
            }
            if (_options.ChatBadges)
            {
                _badgeRenderer.DrawBadges(comment, ref state);
            }
            _textRenderer.DrawUsername(comment, ref state, commentIndex: commentIndex);
            _messageRenderer.DrawMessage(comment, ref state, emotePositionList, highlightWords);

            foreach (var (_, bitmap) in state.SectionImages)
            {
                bitmap.SetImmutable();
            }
        }

        private SKBitmap CombineImages(List<(SKImageInfo info, SKBitmap bitmap)> sectionImages, HighlightType highlightType, int commentIndex)
        {
            SKBitmap finalBitmap = new SKBitmap(_options.ChatWidth, sectionImages.Sum(x => x.info.Height));
            var finalBitmapInfo = finalBitmap.Info;
            var finalCanvas = _bitmapCache.GetOrCreateCanvas(finalBitmap);

            if (highlightType is HighlightType.PayingForward or HighlightType.ChannelPointHighlight or HighlightType.WatchStreak or HighlightType.Combo)
            {
                var accentColor = highlightType is HighlightType.PayingForward
                    ? new SKColor(0xFF26262C) // AARRGGBB
                    : new SKColor(0xFF80808C); // AARRGGBB

                using var paint = new SKPaint { Color = accentColor };
                finalCanvas.DrawRect(_options.SidePadding, 0, _options.AccentStrokeWidth, finalBitmapInfo.Height, paint);
            }
            else if (highlightType is not HighlightType.None)
            {
                const int OPAQUE_THRESHOLD = 245;
                var useAlternateBackground = _options.AlternateMessageBackgrounds && commentIndex % 2 == 1;
                var backgroundColor = useAlternateBackground ? _options.AlternateBackgroundColor : _options.BackgroundColor;
                if (!((!useAlternateBackground && _options.BackgroundColor.Alpha < OPAQUE_THRESHOLD) ||
                    (useAlternateBackground && _options.AlternateBackgroundColor.Alpha < OPAQUE_THRESHOLD)))
                {
                    var highlightBackgroundColor = new SKColor(0x1A6B6B6E); // AARRGGBB
                    using var backgroundPaint = new SKPaint { Color = highlightBackgroundColor };
                    finalCanvas.DrawRect(_options.SidePadding, 0, finalBitmapInfo.Width - _options.SidePadding * 2, finalBitmapInfo.Height, backgroundPaint);
                }

                using var accentPaint = new SKPaint { Color = Purple };
                finalCanvas.DrawRect(_options.SidePadding, 0, _options.AccentStrokeWidth, finalBitmapInfo.Height, accentPaint);
            }

            for (int i = 0; i < sectionImages.Count; i++)
            {
                finalCanvas.DrawBitmap(sectionImages[i].bitmap, 0, i * _options.SectionHeight);
                sectionImages[i].bitmap.Dispose();
            }
            sectionImages.Clear();
            finalCanvas.Flush();
            finalBitmap.SetImmutable();
            return finalBitmap;
        }

        private (SKBitmap frame, bool isCopyFrame) GetFrameFromTick(int currentTick, int sectionDefaultYPos, UpdateFrame currentFrame = null)
        {
            currentFrame ??= GenerateUpdateFrame(currentTick, sectionDefaultYPos);
            var (frame, isCopyFrame) = _emoteRenderer.DrawAnimatedEmotes(currentFrame.Image, currentFrame.Comments, currentTick);
            return (frame, isCopyFrame);
        }

        /// <summary>
        /// Adds a new image section to the section images list and updates the drawing state
        /// </summary>
        public void AddImageSection(ref RenderContext.DrawingState state, Point defaultPos)
        {
            state.DrawPosition.X = defaultPos.X;
            state.DrawPosition.Y = defaultPos.Y;

            // Get chat width and section height from the first image if it exists, otherwise use options
            int chatWidth = state.SectionImages.Count > 0
                ? state.SectionImages[0].info.Width
                : _options.ChatWidth;
            int sectionHeight = state.SectionImages.Count > 0
                ? state.SectionImages[0].info.Height
                : _options.SectionHeight;

            SKBitmap newBitmap = new SKBitmap(chatWidth, sectionHeight);
            SKImageInfo newInfo = newBitmap.Info;
            state.SectionImages.Add((newInfo, newBitmap));

            // Set current canvas to null - it will be retrieved from cache when needed
            state.CurrentCanvas = null;
            
            // Reset layout state for new section
            state.LineStartX = defaultPos.X;
            state.MaxWidth = _options.ChatWidth - _options.SidePadding;
            state.CurrentLineHeight = 0;
        }

        /// <summary>
        /// Checks if adding an element of given width would exceed the line width.
        /// If so, wraps to a new section.
        /// </summary>
        /// <returns>True if wrapped to new section</returns>
        public bool CheckAndWrapIfNeeded(ref RenderContext.DrawingState state, int elementWidth)
        {
            // Check if element would exceed max width
            if (state.DrawPosition.X + elementWidth > state.MaxWidth)
            {
                // Wrap to new section
                AddImageSection(ref state, state.DefaultPosition);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Ensures a valid canvas exists for the current section
        /// </summary>
        public void EnsureCanvas(ref RenderContext.DrawingState state)
        {
            if (state.CurrentCanvas == null && state.SectionImages.Count > 0)
            {
                var currentBitmap = state.SectionImages[state.SectionImages.Count - 1].bitmap;
                state.CurrentCanvas = _bitmapCache.GetOrCreateCanvas(currentBitmap);
            }
        }

        private static void SetFrameMask(SKBitmap frame)
        {
            IntPtr pixelsAddr = frame.GetPixels();
            SKImageInfo frameInfo = frame.Info;
            int height = frameInfo.Height;
            int width = frameInfo.Width;
            unsafe
            {
                byte* ptr = (byte*)pixelsAddr.ToPointer();
                for (int row = 0; row < height; row++)
                {
                    for (int col = 0; col < width; col++)
                    {
                        byte alpha = *(ptr + 3); // alpha of the unmasked pixel
                        *ptr++ = alpha;
                        *ptr++ = alpha;
                        *ptr++ = alpha;
                        *ptr++ = 0xFF;
                    }
                }
            }
        }
    }
}
