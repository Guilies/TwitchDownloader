using SkiaSharp;
using SkiaSharp.HarfBuzz;
using System;
using System.Diagnostics;
using System.Linq;
using TwitchDownloaderCore.ChatRender.Caching;
using TwitchDownloaderCore.ChatRender.Core;
using TwitchDownloaderCore.ChatRender.Utilities;
using TwitchDownloaderCore.Extensions;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.TwitchObjects;
using static TwitchDownloaderCore.ChatRender.Core.RenderContext;

namespace TwitchDownloaderCore.ChatRender.Drawing
{
    /// <summary>
    /// Renders text (usernames and messages) for chat.
    /// Broken up into helper methods for clarity, improved RTL detection logic, and bitmap caching.
    /// </summary>
    public sealed class TextRenderer
    {
        private static readonly SKColor Purple = SKColor.Parse("#7B2CF2");
        private static readonly SKColor[] DefaultUsernameColors =
        {
            SKColor.Parse("#FF0000"), SKColor.Parse("#0000FF"), SKColor.Parse("#00FF00"),
            SKColor.Parse("#B22222"), SKColor.Parse("#FF7F50"), SKColor.Parse("#9ACD32"),
            SKColor.Parse("#FF4500"), SKColor.Parse("#2E8B57"), SKColor.Parse("#DAA520"),
            SKColor.Parse("#D2691E"), SKColor.Parse("#5F9EA0"), SKColor.Parse("#1E90FF"),
            SKColor.Parse("#FF69B4"), SKColor.Parse("#8A2BE2"), SKColor.Parse("#00FF7F")
        };

        private readonly ChatRenderOptions _options;
        private readonly RenderContext _context;
        private readonly FontCache _fontCache;
        // Just for the sake of consistency
        private readonly BitmapCache _bitmapCache;


        // Delegate for adding image sections (injected from RenderContext)
        private readonly AddImageSectionDelegate _addImageSectionCallback;
        private readonly CheckAndWrapDelegate _checkAndWrapCallback;
        private readonly EnsureCanvasDelegate _ensureCanvasCallback;

        public TextRenderer(
            ChatRenderOptions options,
            RenderContext context,
            FontCache fontCache,
            BitmapCache bitmapCache,
            AddImageSectionDelegate addImageSectionCallback,
            CheckAndWrapDelegate checkAndWrapCallback,
            EnsureCanvasDelegate ensureCanvasCallback)
        {
            _options = options;
            _context = context;
            _fontCache = fontCache;
            _bitmapCache = bitmapCache;
            _addImageSectionCallback = addImageSectionCallback ?? throw new ArgumentNullException(nameof(addImageSectionCallback));
            _checkAndWrapCallback = checkAndWrapCallback ?? throw new ArgumentNullException(nameof(checkAndWrapCallback));
            _ensureCanvasCallback = ensureCanvasCallback ?? throw new ArgumentNullException(nameof(ensureCanvasCallback));
        }

        public void DrawUsername(
            Comment comment,
            ref RenderContext.DrawingState state,
            bool appendColon = true,
            SKColor? colorOverride = null,
            int commentIndex = 0)
        {
            var userColor = GetUsernameColor(comment, colorOverride, commentIndex);
            var userName = appendColon ? comment.commenter.display_name + ":" : comment.commenter.display_name;

            using SKPaint userPaint = GetUsernameFont(comment.commenter.display_name);
            userPaint.Color = userColor;

            DrawText(userName, userPaint, padding: true, ref state, highlightWords: false);
            
            // Update DefaultPosition to mark the start of message text (after username)
            state.DefaultPosition.X = state.DrawPosition.X;
        }

        public void DrawText(
        string drawText,
        SKPaint textFont,
        bool padding,
        ref RenderContext.DrawingState state,
        bool highlightWords,
        bool noWrap = false)
        {
            if (string.IsNullOrEmpty(drawText))
                return;

            bool isRtl = TextUtilities.IsRightToLeft(drawText);
            int spacing = padding ? _options.WordSpacing : 0;

            // Debug.WriteLine($"[TextRenderer.DrawText] Text=\"{drawText.Substring(0, Math.Min(30, drawText.Length))}{(drawText.Length > 30 ? "..." : "")}\", Pos=({state.DrawPosition.X},{state.DrawPosition.Y}), MaxWidth={state.MaxWidth}, noWrap={noWrap}");

            // Calculate remaining width using absolute coordinates (MaxWidth is right-edge X coordinate)
            int remainingWidth = state.MaxWidth - state.DrawPosition.X;
            // If for some reason MaxWidth is not initialized, fallback to global value
            if (state.MaxWidth <= 0)
                remainingWidth = (_options.ChatWidth - _options.SidePadding) - state.DrawPosition.X;

            // Fast path: if entire text fits remaining space, draw it
            float measuredWhole = TextUtilities.MeasureText(drawText, textFont, isRtl);
            if (noWrap || measuredWhole + spacing <= remainingWidth)
            {
                // Debug.WriteLine($"[TextRenderer.DrawText] FAST PATH - Text fits. Width={measuredWhole}, Remaining={remainingWidth}");
                
                // Update line height
                var fontMetrics = textFont.FontMetrics;
                int textHeight = (int)Math.Ceiling(Math.Abs(fontMetrics.Ascent) + Math.Abs(fontMetrics.Descent) + fontMetrics.Leading);
                state.CurrentLineHeight = Math.Max(state.CurrentLineHeight, textHeight);

                // Ensure we have a canvas
                _ensureCanvasCallback(ref state);

                if (highlightWords)
                    DrawHighlightBackground(state, measuredWhole, padding);

                if (_options.Outline)
                    DrawTextOutline(drawText, textFont, state, isRtl);

                DrawTextContent(drawText, textFont, state, isRtl);

                state.DrawPosition.X += (int)Math.Floor(measuredWhole + spacing);
                return;
            }

            // Debug.WriteLine($"[TextRenderer.DrawText] ENTERING CHUNK LOOP - Text too wide. Measured={measuredWhole}, Remaining={remainingWidth}");
            
            // CHUNK LOOP: split into prefixes that fit the *remaining* width at the time of drawing
            while (!string.IsNullOrEmpty(drawText))
            {
                remainingWidth = state.MaxWidth - state.DrawPosition.X;
                if (state.MaxWidth <= 0)
                    remainingWidth = (_options.ChatWidth - _options.SidePadding) - state.DrawPosition.X;
                
                // Debug.WriteLine($"[TextRenderer.DrawText] CHUNK ITERATION - RemainingText=\"{drawText.Substring(0, Math.Min(20, drawText.Length))}...\", RemainingWidth={remainingWidth}, Pos=({state.DrawPosition.X},{state.DrawPosition.Y})");

                // If the remaining text now fits, draw the rest
                measuredWhole = TextUtilities.MeasureText(drawText, textFont, isRtl);
                if (measuredWhole + spacing <= remainingWidth)
                {
                    // Debug.WriteLine($"[TextRenderer.DrawText] FINAL CHUNK FITS - Drawing remaining text: \"{drawText}\"");
                    // draw last piece (noWrap true to avoid re-splitting)
                    DrawText(drawText, textFont, padding, ref state, highlightWords, noWrap: true);
                    break;
                }

                // binary-search the largest prefix that fits into remainingWidth
                // OPTIMIZATION: Use span to avoid string allocations during search
                int lo = 0, hi = drawText.Length;
                ReadOnlySpan<char> drawTextSpan = drawText.AsSpan();
                
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) >> 1;
                    ReadOnlySpan<char> prefix = drawTextSpan.Slice(0, mid);
                    
                    // Measure directly from span without ToString() - uses measurement cache
                    if (TextUtilities.MeasureText(prefix, textFont, isRtl) + spacing <= remainingWidth)
                        lo = mid;
                    else
                        hi = mid - 1;
                }

                int take = lo > 0 ? lo : 1;
                string chunk = drawText.Substring(0, take);

                // Debug.WriteLine($"[TextRenderer.DrawText] SPLITTING TEXT - Chunk=\"{chunk}\", ChunkLen={take}, RemainingAfter={drawText.Length - take}");
                
                // draw that chunk (noWrap true)
                DrawText(chunk, textFont, padding, ref state, highlightWords, noWrap: true);

                // Debug.WriteLine($"[TextRenderer.DrawText] AFTER CHUNK DRAW - Pos=({state.DrawPosition.X},{state.DrawPosition.Y})");
                
                // advance to remainder and continue loop
                drawText = drawText.Substring(take);
            }
        }



        /// <summary>
        /// Measures the width of text for layout purposes
        /// </summary>
        public int MeasureTextWidth(string text, SKPaint textFont, bool padding)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
                
            bool isRtl = TextUtilities.IsRightToLeft(text);
            float textWidth = TextUtilities.MeasureText(text, textFont, isRtl);
            int spacing = padding ? _options.WordSpacing : 0;
            return (int)Math.Floor(textWidth + spacing);
        }

        /// <summary>
        /// Determines the color for a username, applying visibility adjustments if needed
        /// </summary>
        private SKColor GetUsernameColor(Comment comment, SKColor? colorOverride, int commentIndex)
        {
            var userColor = colorOverride ?? (comment.message.user_color is not null
                ? SKColor.Parse(comment.message.user_color)
                : DefaultUsernameColors[Math.Abs(comment.commenter.display_name.GetHashCode()) % DefaultUsernameColors.Length]);

            if (colorOverride is null && _options.AdjustUsernameVisibility)
            {
                var useAlternateBackground = _options.AlternateMessageBackgrounds && commentIndex % 2 == 1;
                var backgroundColor = useAlternateBackground
                    ? _options.AlternateBackgroundColor
                    : _options.BackgroundColor;

                userColor = ColorUtilities.AdjustUsernameVisibility(
                    userColor,
                    backgroundColor,
                    _options.Outline,
                    _fontCache.OutlinePaint.Color);
            }

            return userColor;
        }

        /// <summary>
        /// Gets the appropriate font for rendering a username (with fallback for non-ASCII)
        /// </summary>
        private SKPaint GetUsernameFont(string displayName)
        {
            if (displayName.Any(TextUtilities.IsNotAscii))
            {
                char nonAsciiChar = displayName.First(TextUtilities.IsNotAscii);
                return _fontCache.GetFallbackFont(nonAsciiChar).Clone();
            }

            return _fontCache.NameFont.Clone();
        }

        /// <summary>
        /// Draws the highlight background for highlighted words/messages
        /// </summary>
        private void DrawHighlightBackground(RenderContext.DrawingState state, float textWidth, bool padding)
        {
            using var paint = new SKPaint { Color = Purple };
            int spacing = padding ? _options.WordSpacing : 0;
            state.CurrentCanvas.DrawRect(
                state.DrawPosition.X,
                0,
                textWidth + spacing,
                _options.SectionHeight,
                paint);
        }

        /// <summary>
        /// Draws the text outline if outline mode is enabled
        /// </summary>
        private void DrawTextOutline(string text, SKPaint font, RenderContext.DrawingState state, bool isRtl)
        {
            using var outlinePath = isRtl
                ? font.GetShapedTextPath(text, state.DrawPosition.X, state.DrawPosition.Y)
                : font.GetTextPath(text, state.DrawPosition.X, state.DrawPosition.Y);

            state.CurrentCanvas.DrawPath(outlinePath, _fontCache.OutlinePaint);
        }

        /// <summary>
        /// Draws the actual text content
        /// </summary>
        private static void DrawTextContent(string text, SKPaint font, RenderContext.DrawingState state, bool isRtl)
        {
            if (isRtl || TextUtilities.RtlRegex.IsMatch(text))
            {
                state.CurrentCanvas.DrawShapedText(text, state.DrawPosition.X, state.DrawPosition.Y, font);
            }
            else
            {
                state.CurrentCanvas.DrawText(text, state.DrawPosition.X, state.DrawPosition.Y, font);
            }
        }
    }
}