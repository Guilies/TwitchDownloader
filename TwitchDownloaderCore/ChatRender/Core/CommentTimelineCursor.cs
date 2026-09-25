using System;
using System.Collections.Generic;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.ChatRender.Core
{
    /// <summary>
    /// Traverses a chronologically sorted comment list once as render time moves
    /// forward. Construction performs the only binary search needed for a trim.
    /// </summary>
    internal sealed class CommentTimelineCursor
    {
        private readonly IReadOnlyList<Comment> _comments;
        private int _nextCommentIndex;

        internal int LatestCommentIndex => _nextCommentIndex - 1;

        internal CommentTimelineCursor(IReadOnlyList<Comment> comments, double startSeconds)
        {
            _comments = comments ?? throw new ArgumentNullException(nameof(comments));
            _nextCommentIndex = FindFirstCommentAfter(comments, startSeconds);
        }

        internal int AdvanceTo(double currentSeconds)
        {
            while (_nextCommentIndex < _comments.Count &&
                   _comments[_nextCommentIndex].content_offset_seconds <= currentSeconds)
            {
                _nextCommentIndex++;
            }

            return LatestCommentIndex;
        }

        private static int FindFirstCommentAfter(IReadOnlyList<Comment> comments, double seconds)
        {
            var low = 0;
            var high = comments.Count;
            while (low < high)
            {
                var middle = low + ((high - low) >> 1);
                if (comments[middle].content_offset_seconds <= seconds)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }
    }
}
