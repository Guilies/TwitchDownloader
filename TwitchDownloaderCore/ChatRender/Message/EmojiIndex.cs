using System.Collections.Generic;
using System.Linq;
using NeoSmart.Unicode;

namespace TwitchDownloaderCore.ChatRender.Message
{
    /// <summary>
    /// Immutable process-wide index for Unicode emoji sequences. This replaces a
    /// parallel scan of the full emoji catalog for every grapheme.
    /// </summary>
    internal static class EmojiIndex
    {
        private static readonly IReadOnlyDictionary<string, SingleEmoji> BySequence = Emoji.All
            .GroupBy(x => x.Sequence.AsString, System.StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => x.MaxBy(emoji => emoji.SortOrder),
                System.StringComparer.Ordinal);

        internal static SingleEmoji? Find(string textElement)
        {
            if (string.IsNullOrEmpty(textElement))
                return null;

            if (BySequence.TryGetValue(textElement, out var exactMatch))
                return exactMatch;

            // Some grapheme clusters contain a suffix not represented by an
            // asset. Prefer the longest catalog sequence that prefixes it.
            for (var length = textElement.Length - 1; length > 0; length--)
            {
                if (BySequence.TryGetValue(textElement[..length], out var prefixMatch))
                    return prefixMatch;
            }

            return null;
        }
    }
}
