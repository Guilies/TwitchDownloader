using System;
using System.Collections.Generic;
using System.Linq;
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
        private const int DefaultTimestampCapacity = 256;

        private sealed class TimestampCacheEntry
        {
            public SKBitmap Bitmap { get; init; }
            public string Text { get; init; }
            public LinkedListNode<int> RecencyNode { get; init; }
        }

        private readonly int _timestampCapacity;
        private readonly Dictionary<int, TimestampCacheEntry> _timestampBitmaps = new();
        private readonly LinkedList<int> _timestampRecency = new();
        private readonly Dictionary<string, SKBitmap> _avatarBitmaps = new();
        private readonly Dictionary<SKBitmap, SKCanvas> _canvasCache = new();

        public int UsernameBitmapCount => _usernameBitmaps.Count;
        public int BadgeBitmapCount => _badgeBitmaps.Count;
        public int TimestampBitmapCount => _timestampBitmaps.Count;
        public int AvatarBitmapCount => _avatarBitmaps.Count;
        public int CanvasCount => _canvasCache.Count;

        public int TimestampCapacity => _timestampCapacity;

        public BitmapCache(int timestampCapacity = DefaultTimestampCapacity)
        {
            if (timestampCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(timestampCapacity), "Cache capacity must be positive.");

            _timestampCapacity = timestampCapacity;
        }

        public SKCanvas GetOrCreateCanvas(SKBitmap bitmap)
        {
            if (!_canvasCache.TryGetValue(bitmap, out var canvas))
            {
                canvas = new SKCanvas(bitmap);
                _canvasCache[bitmap] = canvas;
            }
            return canvas;
        }

        public void ReleaseCanvas(SKBitmap bitmap)
        {
            if (bitmap is not null && _canvasCache.Remove(bitmap, out var canvas))
                canvas.Dispose();
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
            if (_timestampBitmaps.TryGetValue(wholeSeconds, out var cached))
            {
                _timestampRecency.Remove(cached.RecencyNode);
                _timestampRecency.AddFirst(cached.RecencyNode);
                return (cached.Bitmap, cached.Text);
            }

            var result = factory();
            if (_timestampBitmaps.Count == _timestampCapacity)
                EvictLeastRecentTimestamp();

            var node = _timestampRecency.AddFirst(wholeSeconds);
            _timestampBitmaps[wholeSeconds] = new TimestampCacheEntry
            {
                Bitmap = result.Item1,
                Text = result.Item2,
                RecencyNode = node
            };
            return result;
        }

        private void EvictLeastRecentTimestamp()
        {
            var node = _timestampRecency.Last;
            if (node is null)
                return;

            _timestampRecency.RemoveLast();
            var entry = _timestampBitmaps[node.Value];
            _timestampBitmaps.Remove(node.Value);
            ReleaseCanvas(entry.Bitmap);
            entry.Bitmap?.Dispose();
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

            var ownedBitmaps = new HashSet<SKBitmap>();
            ownedBitmaps.UnionWith(_usernameBitmaps.Values);
            ownedBitmaps.UnionWith(_badgeBitmaps.Values);
            ownedBitmaps.UnionWith(_timestampBitmaps.Values.Select(x => x.Bitmap));
            ownedBitmaps.UnionWith(_avatarBitmaps.Values);
            foreach (var bitmap in ownedBitmaps)
            {
                bitmap?.Dispose();
            }

            _usernameBitmaps.Clear();
            _badgeBitmaps.Clear();
            _timestampBitmaps.Clear();
            _timestampRecency.Clear();
            _avatarBitmaps.Clear();
        }
    }
}
