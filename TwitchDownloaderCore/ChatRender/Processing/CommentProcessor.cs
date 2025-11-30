using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.ChatRender.Processing
{
    /// <summary>
    /// Processes and filters comments before rendering
    /// </summary>
    public sealed class CommentProcessor
    {
        private readonly ChatRenderOptions _options;

        public CommentProcessor(ChatRenderOptions options)
        {
            _options = options;
        }

        public void ProcessComments(List<Comment> comments)
        {
            if (_options.DisperseCommentOffsets)
            {
                DisperseCommentOffsets(comments);
            }

            FloorCommentOffsets(comments);
            RemoveRestrictedComments(comments);
        }

        /// <summary>
        /// Due to Twitch changing the API to return only whole number offsets, renders have become less readable.
        /// To get around this we can disperse comment offsets according to their creation date milliseconds to
        /// help bring back the better readability of comments coming in 1-by-1
        /// </summary>
        private static void DisperseCommentOffsets(List<Comment> comments)
        {
            // Enumerating over a span is faster than a list
            var commentSpan = CollectionsMarshal.AsSpan(comments);

            foreach (var c in commentSpan)
            {
                if (c.content_offset_seconds % 1 == 0 && c.created_at.Millisecond != 0)
                {
                    const int MILLIS_PER_HALF_SECOND = 500;
                    const double MILLIS_PER_SECOND = 1000.0;
                    // Finding the difference between the creation dates and offsets is inconsistent. This approximation looks better more often.
                    c.content_offset_seconds += (c.created_at.Millisecond - MILLIS_PER_HALF_SECOND) / MILLIS_PER_SECOND;
                }
            }
        }

        /// <summary>
        /// Why are we doing this? The question is when to display a 0.5 second offset comment with an update rate of 1.
        /// At the update frame at 0 seconds, or 1 second? We're choosing at 0 seconds here. Flooring to either the
        /// update rate, or if the update rate is greater than 1 just to the next whole number
        /// </summary>
        private void FloorCommentOffsets(List<Comment> comments)
        {
            if (_options.UpdateRate <= 0)
                return;

            foreach (var comment in comments)
            {
                if (_options.UpdateRate > 1)
                {
                    comment.content_offset_seconds = Math.Floor(comment.content_offset_seconds);
                }
                else
                {
                    comment.content_offset_seconds = Math.Floor(comment.content_offset_seconds / _options.UpdateRate) * _options.UpdateRate;
                }
            }
        }

        private void RemoveRestrictedComments(List<Comment> comments)
        {
            if (_options.IgnoreUsersArray.Length == 0 && _options.BannedWordsArray.Length == 0)
            {
                return;
            }

            var ignoredUsers = new HashSet<string>(_options.IgnoreUsersArray, StringComparer.InvariantCultureIgnoreCase);

            Regex bannedWordsRegex = null;
            if (_options.BannedWordsArray.Length > 0)
            {
                var bannedWords = string.Join('|', _options.BannedWordsArray.Select(Regex.Escape));
                bannedWordsRegex = new Regex(@$"(?<=^|[\s\d\p{{P}}\p{{S}}]){bannedWords}(?=$|[\s\d\p{{P}}\p{{S}}])",
              RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            for (var i = comments.Count - 1; i >= 0; i--)
            {
                var comment = comments[i];
                var commenter = comment.commenter;

                if (ignoredUsers.Contains(commenter.name) // ASCII login name
            || (commenter.display_name.Any(IsNotAscii) && ignoredUsers.Contains(commenter.display_name)) // Potentially non-ASCII display name
                      || (bannedWordsRegex is not null && bannedWordsRegex.IsMatch(comment.message.body))) // Banned words
                {
                    comments.RemoveAt(i);
                }
            }
        }

        private static bool IsNotAscii(char input) => input > 127;
    }
}
