using System;
using System.Collections.Generic;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.ChatRender.Processing
{
    internal static class ChatAssetIndex
    {
        internal static HashSet<string> BuildWhitespaceDelimitedTokens(IEnumerable<Comment> comments)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            if (comments is null)
                return tokens;

            foreach (var comment in comments)
            {
                var body = comment.message?.body;
                if (string.IsNullOrWhiteSpace(body))
                    continue;

                foreach (var token in body.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                {
                    tokens.Add(token);
                }
            }

            return tokens;
        }
    }
}
