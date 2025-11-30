using SkiaSharp;
using SkiaSharp.HarfBuzz;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TwitchDownloaderCore.Extensions;

namespace TwitchDownloaderCore.ChatRender.Utilities
{
    public static class TextUtilities
    {
        public static readonly Regex RtlRegex = new("[\u0591-\u07FF\uFB1D-\uFDFD\uFE70-\uFEFC]", RegexOptions.Compiled);

        public static float MeasureText(ReadOnlySpan<char> text, SKPaint textFont, bool? isRtl, SKShaper shaper = null)
        {
            isRtl ??= IsRightToLeft(text);

            if (isRtl == false)
            {
                return textFont.MeasureText(text);
            }

            if (shaper == null)
            {
                return MeasureRtlText(text, textFont);
            }

            return MeasureRtlText(text, textFont, shaper);
        }

        public static float MeasureRtlText(ReadOnlySpan<char> rtlText, SKPaint textFont)
        {
            using var shaper = new SKShaper(textFont.Typeface);
            return MeasureRtlText(rtlText, textFont, shaper);
        }

        public static float MeasureRtlText(ReadOnlySpan<char> rtlText, SKPaint textFont, SKShaper shaper)
        {
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.Add(rtlText, textFont.TextEncoding);
            SKShaper.Result measure = shaper.Shape(buffer, textFont);
            return measure.Width;
        }

        /// <summary>
        /// Produces a <see langword="string"/> less than or equal to <paramref name="maxWidth"/> when drawn with <paramref name="textFont"/> OR substringed to the last index of any character in <paramref name="delimiters"/>.
        /// </summary>
        /// <returns>A shortened in visual width or delimited <see langword="string"/>, whichever comes first.</returns>
        public static ReadOnlySpan<char> SubstringToTextWidth(ReadOnlySpan<char> text, SKPaint textFont, int maxWidth, bool isRtl, ReadOnlySpan<char> delimiters)
        {
            // If we are dealing with non-RTL and don't have any delimiters then SKPaint.BreakText is over 9x faster
            if (!isRtl && text.IndexOfAny(delimiters) == -1)
            {
                return SubstringToTextWidth(text, textFont, maxWidth);
            }

            using var shaper = isRtl
         ? new SKShaper(textFont.Typeface)
        : null;

            // Input text was already less than max width
            if (MeasureText(text, textFont, isRtl, shaper) <= maxWidth)
            {
                return text;
            }

            // Cut in half until <= width
            var length = text.Length;
            do
            {
                length /= 2;
            }
            while (MeasureText(text[..length], textFont, isRtl, shaper) > maxWidth);

            // Add chars until greater than width, then remove the last
            do
            {
                length++;
            } while (MeasureText(text[..length], textFont, isRtl, shaper) < maxWidth);
            text = text[..(length - 1)];

            // Cut at the last delimiter character if applicable
            var delimiterIndex = text.LastIndexOfAny(delimiters);
            if (delimiterIndex != -1)
            {
                return text[..(delimiterIndex + 1)];
            }

            return text;
        }

        /// <summary>
        /// Produces a <see cref="ReadOnlySpan{T}"/> less than or equal to <paramref name="maxWidth"/> when drawn with <paramref name="textFont"/>
        /// </summary>
        /// <returns>A shortened in visual width <see cref="ReadOnlySpan{T}"/>.</returns>
        /// <remarks>This is not compatible with text that needs to be shaped.</remarks>
        public static ReadOnlySpan<char> SubstringToTextWidth(ReadOnlySpan<char> text, SKPaint textFont, int maxWidth)
        {
            var length = (int)textFont.BreakText(text, maxWidth);
            return text[..length];
        }

        public static bool IsRightToLeft(ReadOnlySpan<char> message)
        {
            if (message.Length > 0)
            {
                return message[0] >= '\u0591' && message[0] <= '\u07FF';
            }
            return false;
        }

        public static string[] SwapRightToLeft(string[] words)
        {
            var finalWords = new List<string>(words.Length);
            var rtlStack = new Stack<string>();
            foreach (var word in words)
            {
                if (IsRightToLeft(word))
                {
                    rtlStack.Push(word);
                }
                else
                {
                    while (rtlStack.Count > 0)
                    {
                        finalWords.Add(rtlStack.Pop());
                    }
                    finalWords.Add(word);
                }
            }
            while (rtlStack.Count > 0)
            {
                finalWords.Add(rtlStack.Pop());
            }
            return finalWords.ToArray();
        }

        public static bool IsNotAscii(char input) => input > 127;
    }
}
