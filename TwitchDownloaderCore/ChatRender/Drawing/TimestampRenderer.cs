using System;
using SkiaSharp;
using TwitchDownloaderCore.ChatRender.Caching;
using TwitchDownloaderCore.ChatRender.Core;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.ChatRender.Drawing
{
    /// <summary>
    /// Renders timestamps for chat messages
    /// More defensive checks and bitmap caching added compared to original TwitchDownloader project, with the caveat of a small increase in memory usage (~14MB per hour of chat)
    /// </summary>
    public sealed class TimestampRenderer
    {
        private readonly ChatRenderOptions _options;
        private readonly RenderContext _context;
        private readonly BitmapCache _cache;
        private readonly FontCache _fontCache;

        // Delegate for adding image sections (injected from SectionRenderer)
        private readonly Action<RenderContext.DrawingState, Point> _addImageSectionCallback;

        public TimestampRenderer(
            ChatRenderOptions options,
            RenderContext context,
            BitmapCache cache,
            FontCache fontCache,
            Action<RenderContext.DrawingState, Point> addImageSectionCallback)
        {
            _options = options;
            _context = context;
            _cache = cache;
            _fontCache = fontCache;
            _addImageSectionCallback = addImageSectionCallback ?? throw new ArgumentNullException(nameof(addImageSectionCallback));
        }

        public void DrawTimestamp(Comment comment, ref RenderContext.DrawingState state)
        {
            var timestamp = new TimeSpan(0, 0, (int)comment.content_offset_seconds);
            int wholeSeconds = (int)comment.content_offset_seconds;

            // Get cached or create new timestamp bitmap
            var (timestampBitmap, _) = _cache.GetOrCreateTimestampBitmap(
                wholeSeconds,
                () => CreateTimestampBitmap(timestamp));

            int displayWidth = GetTimestampDisplayWidth(timestamp);

            // Check if we need to wrap to next section
            if (state.DrawPosition.X + displayWidth > _options.ChatWidth - _options.SidePadding * 2)
            {
                _addImageSectionCallback(state, state.DefaultPosition);
            }

            // Ensure we have a valid canvas for the current section bitmap
            if (state.CurrentCanvas == null && state.SectionImages.Count > 0)
            {
                var currentBitmap = state.SectionImages[state.SectionImages.Count - 1].bitmap;
                state.CurrentCanvas = _cache.GetOrCreateCanvas(currentBitmap);
            }

            // Draw the cached timestamp bitmap
            state.CurrentCanvas.DrawBitmap(timestampBitmap, state.DrawPosition.X, 0);

            // Advance position - timestamps use fixed widths for alignment
            state.DrawPosition.X += displayWidth + _options.WordSpacing * 2;
            state.DefaultPosition.X = state.DrawPosition.X;
        }

        /// <summary>
        /// Creates a pre-rendered timestamp bitmap with text and optional outline
        /// </summary>
        private (SKBitmap bitmap, string text) CreateTimestampBitmap(TimeSpan timestamp)
        {
            string formattedTimestamp = FormatTimestamp(timestamp);
            int displayWidth = GetTimestampDisplayWidth(timestamp);

            var bitmap = new SKBitmap(displayWidth, _options.SectionHeight);
            var canvas = _cache.GetOrCreateCanvas(bitmap);

            // Draw outline if enabled
            if (_options.Outline)
            {
                using var outlinePath = _fontCache.MessageFont.GetTextPath(
                    formattedTimestamp,
                    0,
                    _context.SectionBaselineY);
                canvas.DrawPath(outlinePath, _fontCache.OutlinePaint);
            }

            // Draw timestamp text
            canvas.DrawText(
                formattedTimestamp,
                0,
                _context.SectionBaselineY,
                _fontCache.MessageFont);

            canvas.Flush();
            bitmap.SetImmutable();

            return (bitmap, formattedTimestamp);
        }

        /// <summary>
        /// Formats a TimeSpan as a timestamp string (e.g., "1:23:45")
        /// </summary>
        private static string FormatTimestamp(TimeSpan timestamp)
        {
            const int MAX_TIMESTAMP_LENGTH = 8; // "48:00:00"
            Span<char> buffer = stackalloc char[MAX_TIMESTAMP_LENGTH];

            return timestamp.Ticks switch
            {
                >= 24 * TimeSpan.TicksPerHour =>
                    TimeSpanHFormat.ReusableInstance.Format(@"HH\:mm\:ss", timestamp),

                >= 1 * TimeSpan.TicksPerHour =>
                    timestamp.TryFormat(buffer, out var written, @"h\:mm\:ss")
                        ? buffer[..written].ToString()
                        : timestamp.ToString(@"h\:mm\:ss"),

                _ =>
                    timestamp.TryFormat(buffer, out var written, @"m\:ss")
                        ? buffer[..written].ToString()
                        : timestamp.ToString(@"m\:ss")
            };
        }

        /// <summary>
        /// Gets the display width for a timestamp based on its duration
        /// Uses pre-calculated widths to ensure consistent alignment
        /// </summary>
        private int GetTimestampDisplayWidth(TimeSpan timestamp)
        {
            return timestamp.Ticks switch
            {
                >= 10 * TimeSpan.TicksPerHour => _context.TimestampWidths[3],  // "00:00:00"
                >= 1 * TimeSpan.TicksPerHour => _context.TimestampWidths[2],   // "0:00:00"
                >= 10 * TimeSpan.TicksPerMinute => _context.TimestampWidths[1], // "00:00"
                _ => _context.TimestampWidths[0]                                 // "0:00"
            };
        }
    }
}