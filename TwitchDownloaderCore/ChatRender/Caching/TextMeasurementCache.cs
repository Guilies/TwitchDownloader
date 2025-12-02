using SkiaSharp;
using SkiaSharp.HarfBuzz;
using System;
using System.Collections.Generic;
using TwitchDownloaderCore.ChatRender.Utilities;

namespace TwitchDownloaderCore.ChatRender.Caching
{
    /// <summary>
    /// Caches text measurement results using hash-based keys to minimize memory usage.
    /// This reduces redundant expensive font measurement operations during text rendering.
    /// </summary>
    public sealed class TextMeasurementCache
    {
        private readonly struct MeasurementKey : IEquatable<MeasurementKey>
        {
            public readonly int TextHash;        // Hash of the text content
            public readonly int TextLength;      // Length to detect collisions
            public readonly float FontSize;
            public readonly bool IsRtl;

            public MeasurementKey(string text, float fontSize, bool isRtl)
            {
                TextHash = text.GetHashCode();
                TextLength = text.Length;
                FontSize = fontSize;
                IsRtl = isRtl;
            }

            public MeasurementKey(ReadOnlySpan<char> text, float fontSize, bool isRtl)
            {
#if NET6_0_OR_GREATER
                TextHash = string.GetHashCode(text);
#else
                TextHash = text.ToString().GetHashCode();
#endif
                TextLength = text.Length;
                FontSize = fontSize;
                IsRtl = isRtl;
            }

            public bool Equals(MeasurementKey other) =>
                TextHash == other.TextHash &&
                TextLength == other.TextLength &&
                FontSize.Equals(other.FontSize) &&
                IsRtl == other.IsRtl;

            public override bool Equals(object obj) =>
                obj is MeasurementKey other && Equals(other);

            public override int GetHashCode() =>
                HashCode.Combine(TextHash, TextLength, FontSize, IsRtl);
        }

        /// <summary>
        /// Cache value storing width and optional original text for collision detection
        /// </summary>
        private readonly struct CacheValue
        {
            public readonly float Width;
            public readonly string OriginalText; // null unless collision detected

            public CacheValue(float width, string originalText = null)
            {
                Width = width;
                OriginalText = originalText;
            }
        }

        private readonly Dictionary<MeasurementKey, CacheValue> _measurementCache = new(capacity: 2048);
        private readonly object _cacheLock = new();

        /// <summary>
        /// Gets cached measurement or performs measurement and caches result
        /// </summary>
        public float GetOrMeasure(string text, SKPaint font, float fontSize, bool isRtl)
        {
            var key = new MeasurementKey(text, fontSize, isRtl);

            lock (_cacheLock)
            {
                if (_measurementCache.TryGetValue(key, out var cached))
                {
                    // Verify no hash collision (rare)
                    if (cached.OriginalText == null || cached.OriginalText == text)
                    {
                        return cached.Width;
                    }
                    // Hash collision detected - fall through to measure
                }

                // Cache miss or collision - measure and store directly
                // IMPORTANT: Use SKPaint.MeasureText directly to avoid recursion
                float width;
                if (isRtl)
                {
                    // RTL text needs shaping
                    using var shaper = new SKShaper(font.Typeface);
                    using var buffer = new HarfBuzzSharp.Buffer();
                    buffer.AddUtf16(text);
                    var measure = shaper.Shape(buffer, font);
                    width = measure.Width;
                }
                else
                {
                    // Non-RTL text can use simple measurement
                    width = font.MeasureText(text);
                }

                // Implement LRU eviction if cache grows too large
                if (_measurementCache.Count >= 4096)
                {
                    EvictOldestEntries();
                }

                // Store with original text only if collision was detected
                bool hadCollision = _measurementCache.ContainsKey(key) && _measurementCache[key].OriginalText != null;
                var value = new CacheValue(width, hadCollision ? text : null);
                _measurementCache[key] = value;

                return width;
            }
        }

        /// <summary>
        /// Gets cached measurement or performs measurement and caches result (span overload)
        /// </summary>
        public float GetOrMeasure(ReadOnlySpan<char> text, SKPaint font, float fontSize, bool isRtl)
        {
            var key = new MeasurementKey(text, fontSize, isRtl);

            lock (_cacheLock)
            {
                if (_measurementCache.TryGetValue(key, out var cached))
                {
                    // For span-based calls, we can't verify collision without allocating
                    // Trust the cache unless we previously detected a collision
                    if (cached.OriginalText == null)
                    {
                        return cached.Width;
                    }
                    // Collision was detected before - need to verify
                    if (text.SequenceEqual(cached.OriginalText.AsSpan()))
                    {
                        return cached.Width;
                    }
                    // Different text with same hash - fall through to measure
                }

                // Cache miss or collision - measure and store directly
                // IMPORTANT: Use SKPaint.MeasureText directly to avoid recursion
                float width;
                if (isRtl)
                {
                    // RTL text needs shaping
                    using var shaper = new SKShaper(font.Typeface);
                    using var buffer = new HarfBuzzSharp.Buffer();
                    buffer.AddUtf16(text);
                    var measure = shaper.Shape(buffer, font);
                    width = measure.Width;
                }
                else
                {
                    // Non-RTL text can use simple measurement
                    width = font.MeasureText(text);
                }

                // Implement LRU eviction if cache grows too large
                if (_measurementCache.Count >= 4096)
                {
                    EvictOldestEntries();
                }

                // Store with original text only if collision was detected
                bool hadCollision = _measurementCache.ContainsKey(key) && _measurementCache[key].OriginalText != null;
                var value = new CacheValue(width, hadCollision ? text.ToString() : null);
                _measurementCache[key] = value;

                return width;
            }
        }

        /// <summary>
        /// Evicts approximately 25% of cache entries when capacity is exceeded
        /// </summary>
        private void EvictOldestEntries()
        {
            int targetCount = _measurementCache.Count * 3 / 4; // Keep 75%
            int toRemove = _measurementCache.Count - targetCount;

            if (toRemove <= 0)
                return;

            // Simple eviction: remove first N entries
            // Note: Dictionary enumeration order is not guaranteed, but this is sufficient for LRU approximation
            var keysToRemove = new List<MeasurementKey>(toRemove);
            foreach (var key in _measurementCache.Keys)
            {
                keysToRemove.Add(key);
                if (keysToRemove.Count >= toRemove)
                    break;
            }

            foreach (var key in keysToRemove)
            {
                _measurementCache.Remove(key);
            }
        }

        /// <summary>
        /// Clears all cached measurements
        /// </summary>
        public void Clear()
        {
            lock (_cacheLock)
            {
                _measurementCache.Clear();
            }
        }

        /// <summary>
        /// Gets the current number of cached measurements
        /// </summary>
        public int Count
        {
            get
            {
                lock (_cacheLock)
                {
                    return _measurementCache.Count;
                }
            }
        }
    }
}
