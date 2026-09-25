using SkiaSharp;
using SkiaSharp.HarfBuzz;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace TwitchDownloaderCore.ChatRender.Caching
{
    /// <summary>
    /// Fixed-capacity LRU cache for text measurements.
    /// </summary>
    public sealed class TextMeasurementCache
    {
        private const int DefaultCapacity = 4096;

        private readonly struct MeasurementKey : IEquatable<MeasurementKey>
        {
            public readonly int TextHash;
            public readonly int TextLength;
            public readonly int TypefaceId;
            public readonly float TextSize;
            public readonly float TextScaleX;
            public readonly float TextSkewX;
            public readonly bool FakeBoldText;
            public readonly bool IsRtl;

            public MeasurementKey(ReadOnlySpan<char> text, SKPaint font, bool isRtl)
            {
#if NET6_0_OR_GREATER
                TextHash = string.GetHashCode(text);
#else
                TextHash = text.ToString().GetHashCode();
#endif
                TextLength = text.Length;
                TypefaceId = TypefaceIdentity.GetId(font.Typeface);
                TextSize = font.TextSize;
                TextScaleX = font.TextScaleX;
                TextSkewX = font.TextSkewX;
                FakeBoldText = font.FakeBoldText;
                IsRtl = isRtl;
            }

            public bool Equals(MeasurementKey other) =>
                TextHash == other.TextHash &&
                TextLength == other.TextLength &&
                TypefaceId == other.TypefaceId &&
                TextSize.Equals(other.TextSize) &&
                TextScaleX.Equals(other.TextScaleX) &&
                TextSkewX.Equals(other.TextSkewX) &&
                FakeBoldText == other.FakeBoldText &&
                IsRtl == other.IsRtl;

            public override bool Equals(object obj) => obj is MeasurementKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(
                TextHash,
                TextLength,
                TypefaceId,
                TextSize,
                TextScaleX,
                TextSkewX,
                FakeBoldText,
                IsRtl);
        }

        private sealed class CacheEntry
        {
            public MeasurementKey Key { get; init; }
            public string Text { get; init; }
            public float Width { get; init; }
            public LinkedListNode<CacheEntry> RecencyNode { get; set; }
        }

        private sealed class TypefaceIdentity
        {
            private static readonly ConditionalWeakTable<SKTypeface, TypefaceIdentity> Identities = new();
            private static int _nextId;

            private TypefaceIdentity(int id) => Id = id;

            private int Id { get; }

            public static int GetId(SKTypeface typeface)
            {
                if (typeface is null)
                    return 0;

                return Identities.GetValue(
                    typeface,
                    static _ => new TypefaceIdentity(Interlocked.Increment(ref _nextId))).Id;
            }
        }

        private readonly int _capacity;
        private readonly Dictionary<MeasurementKey, List<CacheEntry>> _measurementCache;
        private readonly LinkedList<CacheEntry> _recency = new();
        private readonly object _cacheLock = new();

        public TextMeasurementCache(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Cache capacity must be positive.");

            _capacity = capacity;
            _measurementCache = new Dictionary<MeasurementKey, List<CacheEntry>>(capacity);
        }

        public int Capacity => _capacity;

        /// <summary>
        /// Gets a cached string measurement or measures and retains an exact copy of the key text.
        /// </summary>
        public float GetOrMeasure(string text, SKPaint font, float fontSize, bool isRtl)
        {
            if (text is null)
                throw new ArgumentNullException(nameof(text));
            if (font is null)
                throw new ArgumentNullException(nameof(font));

            return GetOrMeasureCore(text.AsSpan(), text, font, isRtl);
        }

        /// <summary>
        /// Gets a cached span measurement without allocating on cache hits.
        /// </summary>
        public float GetOrMeasure(ReadOnlySpan<char> text, SKPaint font, float fontSize, bool isRtl)
        {
            if (font is null)
                throw new ArgumentNullException(nameof(font));

            return GetOrMeasureCore(text, null, font, isRtl);
        }

        private float GetOrMeasureCore(ReadOnlySpan<char> text, string originalText, SKPaint font, bool isRtl)
        {
            var key = new MeasurementKey(text, font, isRtl);

            lock (_cacheLock)
            {
                if (_measurementCache.TryGetValue(key, out var bucket))
                {
                    foreach (var entry in bucket)
                    {
                        if (!text.SequenceEqual(entry.Text.AsSpan()))
                            continue;

                        Touch(entry);
                        return entry.Width;
                    }
                }

                float width = Measure(text, font, isRtl);
                Add(key, originalText ?? text.ToString(), width, bucket);
                return width;
            }
        }

        private static float Measure(ReadOnlySpan<char> text, SKPaint font, bool isRtl)
        {
            if (!isRtl)
                return font.MeasureText(text);

            using var shaper = new SKShaper(font.Typeface);
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf16(text);
            buffer.GuessSegmentProperties();
            return shaper.Shape(buffer, font).Width;
        }

        private void Add(MeasurementKey key, string text, float width, List<CacheEntry> bucket)
        {
            if (_recency.Count == _capacity)
                EvictLeastRecentlyUsed();

            if (bucket is null || !_measurementCache.ContainsKey(key))
            {
                bucket = new List<CacheEntry>(1);
                _measurementCache[key] = bucket;
            }

            var entry = new CacheEntry { Key = key, Text = text, Width = width };
            entry.RecencyNode = _recency.AddFirst(entry);
            bucket.Add(entry);
        }

        private void Touch(CacheEntry entry)
        {
            _recency.Remove(entry.RecencyNode);
            _recency.AddFirst(entry.RecencyNode);
        }

        private void EvictLeastRecentlyUsed()
        {
            var node = _recency.Last;
            if (node is null)
                return;

            var entry = node.Value;
            _recency.RemoveLast();

            var bucket = _measurementCache[entry.Key];
            bucket.Remove(entry);
            if (bucket.Count == 0)
                _measurementCache.Remove(entry.Key);
        }

        public void Clear()
        {
            lock (_cacheLock)
            {
                _measurementCache.Clear();
                _recency.Clear();
            }
        }

        public int Count
        {
            get
            {
                lock (_cacheLock)
                {
                    return _recency.Count;
                }
            }
        }
    }
}
