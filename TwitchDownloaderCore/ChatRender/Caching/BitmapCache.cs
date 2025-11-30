using System;
using System.Collections.Generic;
using SkiaSharp;

namespace TwitchDownloaderCore.ChatRender.Caching
{
    /// <summary>
    /// Manages pre-rendered bitmap fragments with canvas pooling
    /// </summary>
    public sealed class BitmapCache : IDisposable
    {
        private readonly Dictionary<string, SKBitmap> _usernameBitmaps = new();
        private readonly Dictionary<string, SKBitmap> _badgeBitmaps = new();
        private readonly Dictionary<int, (SKBitmap bitmap, string text)> _timestampBitmaps = new();
        private readonly Dictionary<string, SKBitmap> _avatarBitmaps = new();
        private readonly Dictionary<SKBitmap, SKCanvas> _canvasCache = new();

        public SKCanvas GetOrCreateCanvas(SKBitmap bitmap)
        {
            if (!_canvasCache.TryGetValue(bitmap, out var canvas))
            {
                canvas = new SKCanvas(bitmap);
                _canvasCache[bitmap] = canvas;
            }
            return canvas;
        }

        public SKBitmap GetOrCreateUsernameBitmap(string cacheKey, Func<SKBitmap> factory)
        {
            if (!_usernameBitmaps.TryGetValue(cacheKey, out var bitmap))
            {
                bitmap = factory();
                _usernameBitmaps[cacheKey] = bitmap;
            }
            return bitmap;
        }

        public SKBitmap GetOrCreateBadgesBitmap(string cacheKey, Func<SKBitmap> factory)
        {
            if (!_badgeBitmaps.TryGetValue(cacheKey, out var bitmap))
            {
                bitmap = factory();
                _badgeBitmaps[cacheKey] = bitmap;
            }
            return bitmap;
        }

        public (SKBitmap bitmap, string text) GetOrCreateTimestampBitmap(int wholeSeconds, Func<(SKBitmap, string)> factory)
        {
            if (!_timestampBitmaps.TryGetValue(wholeSeconds, out var result))
            {
                result = factory();
                _timestampBitmaps[wholeSeconds] = result;
            }
            return result;
        }

        public SKBitmap GetOrCreateAvatarBitmap(string avatarUrl, Func<SKBitmap> factory)
        {
            if (!_avatarBitmaps.TryGetValue(avatarUrl, out var bitmap))
            {
                bitmap = factory();
                _avatarBitmaps[avatarUrl] = bitmap;
            }
            return bitmap;
        }

        public void Dispose()
        {
            // Dispose all canvases
            foreach (var canvas in _canvasCache.Values)
            {
                canvas?.Dispose();
            }
            _canvasCache.Clear();

            // Note: We don't dispose the bitmaps themselves as they may still be in use
            // The owner of the BitmapCache should manage bitmap disposal
            _usernameBitmaps.Clear();
            _badgeBitmaps.Clear();
            _timestampBitmaps.Clear();
            _avatarBitmaps.Clear();
        }
    }
}
