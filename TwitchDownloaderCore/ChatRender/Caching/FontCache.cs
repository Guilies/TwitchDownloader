using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Options;

namespace TwitchDownloaderCore.ChatRender.Caching
{
    /// <summary>
    /// Manages fonts and fallback font lookup
    /// </summary>
    public sealed class FontCache : IDisposable
    {
        private readonly ChatRenderOptions _options;
        private readonly ITaskProgress _progress;
        private readonly Dictionary<int, SKPaint> _fallbackFontCache = new();
        private bool _noFallbackFontFound = false;
        private readonly SKFontManager _fontManager;

        public SKPaint MessageFont { get; private set; }
        public SKPaint NameFont { get; private set; }
        public SKPaint OutlinePaint { get; private set; }

        public FontCache(ChatRenderOptions options, ITaskProgress progress)
        {
            _options = options;
            _progress = progress;
            _fontManager = SKFontManager.CreateDefault();
            InitializeFonts();
        }

        private void InitializeFonts()
        {
            // Initialize outline paint
            OutlinePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)(_options.OutlineSize * _options.ReferenceScale),
                StrokeJoin = SKStrokeJoin.Round,
                Color = SKColors.Black,
                IsAntialias = true,
                IsAutohinted = true,
                LcdRenderText = true,
                SubpixelText = true,
                HintingLevel = SKPaintHinting.Full,
                FilterQuality = SKFilterQuality.High
            };

            // Initialize name font
            NameFont = new SKPaint
            {
                LcdRenderText = true,
                SubpixelText = true,
                TextSize = (float)_options.FontSize,
                IsAntialias = true,
                IsAutohinted = true,
                HintingLevel = SKPaintHinting.Full,
                FilterQuality = SKFilterQuality.High
            };

            // Initialize message font
            MessageFont = new SKPaint
            {
                LcdRenderText = true,
                SubpixelText = true,
                TextSize = (float)_options.FontSize,
                IsAntialias = true,
                IsAutohinted = true,
                HintingLevel = SKPaintHinting.Full,
                FilterQuality = SKFilterQuality.High,
                Color = _options.MessageColor
            };
        }

        public void SetTypefaces(SKFontStyle usernameFontStyle, SKFontStyle messageFontStyle)
        {
            if (_options.Font == "Inter Embedded")
            {
                NameFont.Typeface = GetInterTypeface(usernameFontStyle);
                MessageFont.Typeface = GetInterTypeface(messageFontStyle);
            }
            else
            {
                NameFont.Typeface = SKTypeface.FromFamilyName(_options.Font, usernameFontStyle);
                MessageFont.Typeface = SKTypeface.FromFamilyName(_options.Font, messageFontStyle);
            }
        }

        public SKPaint GetFallbackFont(int input)
        {
            ref var fallbackPaint = ref CollectionsMarshal.GetValueRefOrAddDefault(_fallbackFontCache, input, out bool alreadyExists);
            if (alreadyExists)
            {
                return fallbackPaint;
            }

            SKPaint newPaint = new SKPaint
            {
                Typeface = _fontManager.MatchCharacter(input),
                LcdRenderText = true,
                TextSize = (float)_options.FontSize,
                IsAntialias = true,
                SubpixelText = true,
                IsAutohinted = true,
                HintingLevel = SKPaintHinting.Full,
                FilterQuality = SKFilterQuality.High
            };

            if (newPaint.Typeface == null)
            {
                newPaint.Typeface = SKTypeface.Default;
                if (!_noFallbackFontFound)
                {
                    _noFallbackFontFound = true;
                    _progress.LogWarning("No valid typefaces were found for some messages.");
                }
            }

            fallbackPaint = newPaint;
            return newPaint;
        }

        private static SKTypeface GetInterTypeface(SKFontStyle fontStyle)
        {
            MemoryStream stream = null;
            try
            {
                if (fontStyle == SKFontStyle.Bold)
                    stream = new MemoryStream(Properties.Resources.InterBold);
                else if (fontStyle == SKFontStyle.Italic)
                    stream = new MemoryStream(Properties.Resources.InterItalic);
                else if (fontStyle == SKFontStyle.BoldItalic)
                    stream = new MemoryStream(Properties.Resources.InterBoldItalic);
                else
                    stream = new MemoryStream(Properties.Resources.Inter);

                return SKTypeface.FromStream(stream);
            }
            finally
            {
                stream?.Dispose();
            }
        }

        public void Dispose()
        {
            foreach (var (_, paint) in _fallbackFontCache)
            {
                paint?.Dispose();
            }
            _fallbackFontCache.Clear();

            _fontManager?.Dispose();
            NameFont?.Dispose();
            MessageFont?.Dispose();
            OutlinePaint?.Dispose();
        }
    }
}
