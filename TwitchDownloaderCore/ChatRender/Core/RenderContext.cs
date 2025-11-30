using System;
using System.Collections.Generic;
using SkiaSharp;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.ChatRender.Core
{
    /// <summary>
    /// Stores pre-calculated geometry and current drawing state
    /// </summary>
    public sealed class RenderContext : IDisposable
    {
        private readonly ChatRenderOptions _options;

        // Pre-calculated geometry (set once, read many)
        public int SectionBaselineY { get; private set; }
        public float SectionVerticalCenter => _options.SectionHeight / 2f;
        public float BlockArtCharWidth { get; private set; }
        public int[] TimestampWidths { get; private set; }

        // Drawing state struct
        public struct DrawingState
        {
            public List<(SKImageInfo info, SKBitmap bitmap)> SectionImages;
            public SKCanvas CurrentCanvas;
            public Point DrawPosition;
            public Point DefaultPosition;
            
            // Layout state for line wrapping (managed by SectionRenderer)
            public int LineStartX;      // X position where current line started
            public int MaxWidth;        // Maximum allowed X position
            public int CurrentLineHeight; // Height of current line (max of all elements on line)
        }

        public RenderContext(ChatRenderOptions options)
        {
            _options = options;
        }

        public void InitializeGeometry(SKPaint messageFont)
        {
            // Calculate SectionBaselineY from messageFont.MeasureText("ABC123")
            SKRect sampleTextBounds = new SKRect();
            messageFont.MeasureText("ABC123", ref sampleTextBounds);
            SectionBaselineY = (int)(((_options.SectionHeight - sampleTextBounds.Height) / 2.0) + sampleTextBounds.Height);

            // Rough estimation of the width of a single block art character
            using (var fallbackFont = new SKPaint
            {
                Typeface = SKFontManager.CreateDefault().MatchCharacter('█'),
                TextSize = (float)_options.FontSize,
                LcdRenderText = true,
                IsAntialias = true,
                SubpixelText = true,
                IsAutohinted = true,
                HintingLevel = SKPaintHinting.Full,
                FilterQuality = SKFilterQuality.High
            })
            {
                if (fallbackFont.Typeface == null)
                    fallbackFont.Typeface = SKTypeface.Default;

                BlockArtCharWidth = fallbackFont.MeasureText("█");
            }

            // Cache the rendered timestamp widths
            TimestampWidths = !_options.Timestamp ? Array.Empty<int>() : new[]
            {
                (int)messageFont.MeasureText("0:00"),
                (int)messageFont.MeasureText("00:00"),
                (int)messageFont.MeasureText("0:00:00"),
                (int)messageFont.MeasureText("00:00:00")
            };
        }

        public void Dispose()
        {
            // No resources to dispose currently, but keeping for future extensibility
        }
    }
}
