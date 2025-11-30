using NeoSmart.Unicode;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.ChatRender.Caching;
using TwitchDownloaderCore.ChatRender.Core;
using TwitchDownloaderCore.ChatRender.Drawing;
using TwitchDownloaderCore.ChatRender.Utilities;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.ChatRender.Message
{
    /// <summary>
    /// Orchestrates rendering of chat messages by delegating to text and emote renderers
    /// </summary>
    public sealed class MessageRenderer
    {
        private static readonly SKColor Purple = SKColor.Parse("#7B2CF2");

        // TODO: Use FrozenDictionary when .NET 8
        private static readonly IReadOnlyDictionary<int, string> AllEmojiSequences = Emoji.All.ToDictionary(e => e.SortOrder, e => e.Sequence.AsString);

        private readonly ChatRenderOptions _options;
        private readonly RenderContext _context;
        private readonly ImageCache _imageCache;
        private readonly FontCache _fontCache;
        private readonly BitmapCache _bitmapCache;
        private readonly TextRenderer _textRenderer;
        private readonly EmoteRenderer _emoteRenderer;

        // Delegate for adding image sections (injected from SectionRenderer)
        private readonly Action<RenderContext.DrawingState, Point> _addImageSectionCallback;
        private readonly Func<RenderContext.DrawingState, int, bool> _checkAndWrapCallback;
        private readonly Action<RenderContext.DrawingState> _ensureCanvasCallback;

        public MessageRenderer(
            ChatRenderOptions options,
            RenderContext context,
            ImageCache imageCache,
            FontCache fontCache,
            BitmapCache bitmapCache,
            TextRenderer textRenderer,
            EmoteRenderer emoteRenderer,
            Action<RenderContext.DrawingState, Point> addImageSectionCallback,
            Func<RenderContext.DrawingState, int, bool> checkAndWrapCallback,
            Action<RenderContext.DrawingState> ensureCanvasCallback)
        {
            _options = options;
            _context = context;
            _imageCache = imageCache;
            _fontCache = fontCache;
            _bitmapCache = bitmapCache;
            _textRenderer = textRenderer;
            _emoteRenderer = emoteRenderer;
            _addImageSectionCallback = addImageSectionCallback ?? throw new ArgumentNullException(nameof(addImageSectionCallback));
            _checkAndWrapCallback = checkAndWrapCallback ?? throw new ArgumentNullException(nameof(checkAndWrapCallback));
            _ensureCanvasCallback = ensureCanvasCallback ?? throw new ArgumentNullException(nameof(ensureCanvasCallback));
        }

        public void DrawMessage(Comment comment, ref RenderContext.DrawingState state, List<(Point, TwitchEmote)> emotePositionList, bool highlightWords)
        {
            int bitsCount = comment.message.bits_spent;
            foreach (var fragment in comment.message.fragments)
            {
                if (fragment.emoticon == null)
                {
                    // Either text or third party emote
                    var fragmentParts = TextUtilities.SwapRightToLeft(fragment.text.Split(' '));
                    foreach (var fragmentString in fragmentParts)
                    {
                        if (string.IsNullOrEmpty(fragmentString))
                            continue;

                        DrawFragmentPart(ref state, emotePositionList, bitsCount, fragmentString, highlightWords);
                    }
                }
                else
                {
                    _emoteRenderer.DrawFirstPartyEmote(fragment, ref state, emotePositionList, highlightWords);
                }
            }
        }

        private void DrawFragmentPart(
            ref RenderContext.DrawingState state,
            List<(Point, TwitchEmote)> emotePositionList,
            int bitsCount,
            string fragmentPart,
            bool highlightWords,
            bool skipThird = false,
            bool skipEmoji = false,
            bool skipNonFont = false)
        {
            if (!skipThird && TryGetTwitchEmote(_imageCache.ThirdPartyEmotes, fragmentPart, out var emote))
            {
                // Check wrap before drawing emote
                int emoteWidth = emote.Info.Width + _options.EmoteSpacing;
                _checkAndWrapCallback(state, emoteWidth);
                
                _emoteRenderer.DrawThirdPartyEmote(emote, ref state, emotePositionList, highlightWords);
            }
            else if (!skipEmoji && RegexUtility.EmojiRegex.IsMatch(fragmentPart))
            {
                DrawEmojiMessage(ref state, emotePositionList, bitsCount, fragmentPart, highlightWords);
            }
            else if (!skipNonFont && (!_fontCache.MessageFont.ContainsGlyphs(fragmentPart) || new StringInfo(fragmentPart).LengthInTextElements < fragmentPart.Length))
            {
                DrawNonFontMessage(ref state, bitsCount, fragmentPart, highlightWords);
            }
            else
            {
                DrawRegularMessage(ref state, emotePositionList, bitsCount, fragmentPart, highlightWords);
            }
        }

        private void DrawRegularMessage(
            ref RenderContext.DrawingState state,
            List<(Point, TwitchEmote)> emotePositionList,
            int bitsCount,
            string fragmentString,
            bool highlightWords)
        {
            // Try to render as a cheer emote if bits are present
            if (bitsCount > 0 && TryDrawCheerEmote(ref state, emotePositionList, fragmentString))
            {
                return;
            }

            // Check wrap before drawing text
            int textWidth = _textRenderer.MeasureTextWidth(fragmentString, _fontCache.MessageFont, true);
            _checkAndWrapCallback(state, textWidth);

            // Fall back to regular text
            _textRenderer.DrawText(fragmentString, _fontCache.MessageFont, true, ref state, highlightWords);
        }

        /// <summary>
        /// Attempts to render a fragment as a cheer emote
        /// </summary>
        private bool TryDrawCheerEmote(
            ref RenderContext.DrawingState state,
            List<(Point, TwitchEmote)> emotePositionList,
            string fragmentString)
        {
            // Check if fragment contains both letters and digits
            if (!fragmentString.Any(char.IsDigit) || !fragmentString.Any(char.IsLetter))
            {
                return false;
            }

            int bitsIndex = fragmentString.AsSpan().IndexOfAny("0123456789");
            if (bitsIndex == -1)
            {
                return false;
            }

            // Try to parse bits amount and find matching cheer emote
            if (!int.TryParse(fragmentString.AsSpan(bitsIndex), out var bitsAmount))
            {
                return false;
            }

            if (!TryGetCheerEmote(_imageCache.Cheermotes, fragmentString.AsSpan(0, bitsIndex), out var cheerEmote))
            {
                return false;
            }

            // Get the appropriate tier and render
            var tier = cheerEmote.getTier(bitsAmount);
            var emote = tier.Value;
            var emoteInfo = emote.Info;

            // Check wrap before drawing emote
            int emoteWidth = emoteInfo.Width + _options.EmoteSpacing;
            _checkAndWrapCallback(state, emoteWidth);

            // Ensure we have a valid canvas for the current section bitmap
            _ensureCanvasCallback(state);

            Point emotePoint = new Point
            {
                X = state.DrawPosition.X,
                Y = CalculateEmoteVerticalPosition(state, emoteInfo.Height)
            };

            emotePositionList.Add((emotePoint, emote));
            state.DrawPosition.X += emoteInfo.Width + _options.EmoteSpacing;

            return true;
        }

        private void DrawEmojiMessage(
            ref RenderContext.DrawingState state,
            List<(Point, TwitchEmote)> emotePositionList,
            int bitsCount,
            string fragmentString,
            bool highlightWords)
        {
            if (_options.EmojiVendor == EmojiVendor.None)
            {
                DrawFragmentPart(ref state, emotePositionList, bitsCount, fragmentString, highlightWords, skipThird: true, skipEmoji: true);
                return;
            }

            var enumerator = StringInfo.GetTextElementEnumerator(fragmentString);
            var nonEmojiBuffer = new StringBuilder();

            while (enumerator.MoveNext())
            {
                var textElement = enumerator.GetTextElement();

                // ASCII characters are not emojis
                if (textElement.Length == 1 && char.IsAscii(textElement[0]))
                {
                    nonEmojiBuffer.Append(textElement);
                    continue;
                }

                // Try to find matching emoji
                var matchedEmoji = FindMatchingEmoji(textElement);
                if (matchedEmoji == null)
                {
                    nonEmojiBuffer.Append(textElement);
                    continue;
                }

                // Flush any buffered non-emoji text
                if (nonEmojiBuffer.Length > 0)
                {
                    DrawFragmentPart(ref state, emotePositionList, bitsCount, nonEmojiBuffer.ToString(), highlightWords, skipThird: true, skipEmoji: true);
                    nonEmojiBuffer.Clear();
                }

                // Draw the emoji
                DrawSingleEmoji(ref state, matchedEmoji.Value, highlightWords);
            }

            // Flush remaining buffered text
            if (nonEmojiBuffer.Length > 0)
            {
                DrawFragmentPart(ref state, emotePositionList, bitsCount, nonEmojiBuffer.ToString(), highlightWords, skipThird: true, skipEmoji: true);
            }
        }

        /// <summary>
        /// Finds a matching emoji in the cache for the given text element
        /// </summary>
        private SingleEmoji? FindMatchingEmoji(string textElement)
        {
            var emojiBag = new ConcurrentBag<SingleEmoji>();

            // Parallel search for matching emojis
            Emoji.All.AsParallel()
                .Where(emoji => textElement.StartsWith(AllEmojiSequences[emoji.SortOrder]))
                .ForAll(emoji =>
                {
                    // Special handling for flags - require exact match
                    if (emoji.Group == "Flags")
                    {
                        if (textElement.StartsWith(AllEmojiSequences[emoji.SortOrder], StringComparison.Ordinal))
                        {
                            emojiBag.Add(emoji);
                        }
                    }
                    else
                    {
                        emojiBag.Add(emoji);
                    }
                });

            if (emojiBag.IsEmpty)
            {
                return null;
            }

            // Filter to only emojis that exist in our cache
            var validMatches = emojiBag
                .Where(emoji => _imageCache.Emojis.ContainsKey(GeometryUtilities.GetKeyName(emoji.Sequence.Codepoints)))
                .ToList();

            if (validMatches.Count == 0)
            {
                return null;
            }

            // Return the most specific match (highest sort order)
            return validMatches.MaxBy(x => x.SortOrder);
        }

        /// <summary>
        /// Draws a single emoji bitmap
        /// </summary>
        private void DrawSingleEmoji(ref RenderContext.DrawingState state, SingleEmoji emoji, bool highlightWords)
        {
            var emojiKey = GeometryUtilities.GetKeyName(emoji.Sequence.Codepoints);
            var emojiImage = _imageCache.Emojis[emojiKey];
            var emojiInfo = emojiImage.Info;

            // Check wrap before drawing emoji
            int emojiWidth = emojiInfo.Width + _options.EmoteSpacing;
            _checkAndWrapCallback(state, emojiWidth);

            // Ensure we have a valid canvas for the current section bitmap
            _ensureCanvasCallback(state);

            Point emotePoint = new Point
            {
                X = state.DrawPosition.X + (int)Math.Ceiling(_options.EmoteSpacing / 2d),
                Y = (int)((_options.SectionHeight - emojiInfo.Height) / 2.0)
            };

            // Draw highlight background if needed
            if (highlightWords)
            {
                using var paint = new SKPaint { Color = Purple };
                state.CurrentCanvas.DrawRect(
                    (int)(emotePoint.X - _options.EmoteSpacing / 2d),
                    0,
                    emojiInfo.Width + _options.EmoteSpacing,
                    _options.SectionHeight,
                    paint);
            }

            state.CurrentCanvas.DrawBitmap(emojiImage, emotePoint.X, emotePoint.Y);
            state.DrawPosition.X += emojiInfo.Width + _options.EmoteSpacing;
        }

        private void DrawNonFontMessage(
            ref RenderContext.DrawingState state,
            int bitsCount,
            string fragmentString,
            bool highlightWords)
        {
            ReadOnlySpan<char> fragmentSpan = fragmentString.AsSpan().Trim('\uFE0F');

            // Handle block art wrapping
            if (RegexUtility.BlockArtRegex.IsMatch(fragmentString))
            {
                int textWidth = (int)(fragmentSpan.Length * _context.BlockArtCharWidth);
                if (_options.BlockArtPreWrap && state.DrawPosition.X + textWidth > _options.BlockArtPreWrapWidth)
                {
                    _addImageSectionCallback(state, state.DefaultPosition);
                }
            }

            // Process character by character, switching fonts as needed
            var inFontBuffer = new StringBuilder();
            var nonFontBuffer = new StringBuilder();

            for (int j = 0; j < fragmentSpan.Length; j++)
            {
                // Handle surrogate pairs
                if (char.IsHighSurrogate(fragmentSpan[j]) && j + 1 < fragmentSpan.Length && char.IsLowSurrogate(fragmentSpan[j + 1]))
                {
                    FlushBuffers(ref state, inFontBuffer, nonFontBuffer, highlightWords, padding: false);

                    int utf32Char = char.ConvertToUtf32(fragmentSpan[j], fragmentSpan[j + 1]);

                    // Don't attempt to draw U+E0000
                    if (utf32Char != 0xE0000)
                    {
                        using var font = _fontCache.GetFallbackFont(utf32Char).Clone();
                        font.Color = _options.MessageColor;
                        
                        // Check wrap before drawing
                        int charWidth = _textRenderer.MeasureTextWidth(fragmentSpan.Slice(j, 2).ToString(), font, false);
                        _checkAndWrapCallback(state, charWidth);
                        
                        _textRenderer.DrawText(fragmentSpan.Slice(j, 2).ToString(), font, false, ref state, highlightWords);
                    }

                    j++; // Skip the low surrogate
                }
                // Check if character is in message font
                else if (!_fontCache.MessageFont.ContainsGlyphs(fragmentSpan.Slice(j, 1)) ||
                         new StringInfo(fragmentSpan[j].ToString()).LengthInTextElements == 0)
                {
                    // Character not in font - buffer it for fallback font
                    if (inFontBuffer.Length > 0)
                    {
                        // Check wrap before drawing buffered text
                        int textWidth = _textRenderer.MeasureTextWidth(inFontBuffer.ToString(), _fontCache.MessageFont, false);
                        _checkAndWrapCallback(state, textWidth);
                        
                        _textRenderer.DrawText(inFontBuffer.ToString(), _fontCache.MessageFont, false, ref state, highlightWords);
                        inFontBuffer.Clear();
                    }
                    nonFontBuffer.Append(fragmentSpan[j]);
                }
                else
                {
                    // Character is in font - buffer it for message font
                    if (nonFontBuffer.Length > 0)
                    {
                        using var font = _fontCache.GetFallbackFont(nonFontBuffer[0]).Clone();
                        font.Color = _options.MessageColor;
                        
                        // Check wrap before drawing buffered text
                        int textWidth = _textRenderer.MeasureTextWidth(nonFontBuffer.ToString(), font, false);
                        _checkAndWrapCallback(state, textWidth);
                        
                        _textRenderer.DrawText(nonFontBuffer.ToString(), font, false, ref state, highlightWords);
                        nonFontBuffer.Clear();
                    }
                    inFontBuffer.Append(fragmentSpan[j]);
                }
            }

            // Flush remaining buffers with padding
            FlushBuffers(ref state, inFontBuffer, nonFontBuffer, highlightWords, padding: true);
        }

        /// <summary>
        /// Flushes any buffered text using appropriate fonts
        /// </summary>
        private void FlushBuffers(
            ref RenderContext.DrawingState state,
            StringBuilder inFontBuffer,
            StringBuilder nonFontBuffer,
            bool highlightWords,
            bool padding)
        {
            if (nonFontBuffer.Length > 0)
            {
                using var font = _fontCache.GetFallbackFont(nonFontBuffer[0]).Clone();
                font.Color = _options.MessageColor;
                
                // Check wrap before drawing
                int textWidth = _textRenderer.MeasureTextWidth(nonFontBuffer.ToString(), font, padding);
                _checkAndWrapCallback(state, textWidth);
                
                _textRenderer.DrawText(nonFontBuffer.ToString(), font, padding, ref state, highlightWords);
                nonFontBuffer.Clear();
            }

            if (inFontBuffer.Length > 0)
            {
                // Check wrap before drawing
                int textWidth = _textRenderer.MeasureTextWidth(inFontBuffer.ToString(), _fontCache.MessageFont, padding);
                _checkAndWrapCallback(state, textWidth);
                
                _textRenderer.DrawText(inFontBuffer.ToString(), _fontCache.MessageFont, padding, ref state, highlightWords);
                inFontBuffer.Clear();
            }
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

        /// <summary>
        /// Binary search for Twitch emote by name
        /// </summary>
        private static bool TryGetTwitchEmote(
            List<TwitchEmote> twitchEmoteList,
            ReadOnlySpan<char> emoteName,
            [NotNullWhen(true)] out TwitchEmote twitchEmote)
        {
            var emoteListSpan = CollectionsMarshal.AsSpan(twitchEmoteList);
            var lo = 0;
            var hi = emoteListSpan.Length - 1;

            while (lo <= hi)
            {
                var i = lo + ((hi - lo) >> 1);
                var order = emoteListSpan[i].Name.AsSpan().CompareTo(emoteName, StringComparison.Ordinal);

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

        /// <summary>
        /// Binary search for cheer emote by prefix
        /// </summary>
        private static bool TryGetCheerEmote(
            List<CheerEmote> cheerEmoteList,
            ReadOnlySpan<char> prefix,
            [NotNullWhen(true)] out CheerEmote cheerEmote)
        {
            var emoteListSpan = CollectionsMarshal.AsSpan(cheerEmoteList);
            var lo = 0;
            var hi = emoteListSpan.Length - 1;

            while (lo <= hi)
            {
                var i = lo + ((hi - lo) >> 1);
                var order = emoteListSpan[i].prefix.AsSpan().CompareTo(prefix, StringComparison.Ordinal);

                if (order == 0)
                {
                    cheerEmote = emoteListSpan[i];
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

            cheerEmote = null;
            return false;
        }
    }
}