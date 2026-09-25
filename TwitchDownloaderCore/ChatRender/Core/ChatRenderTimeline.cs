using System;

namespace TwitchDownloaderCore.ChatRender.Core
{
    internal static class ChatRenderTimeline
    {
        internal static (int StartTick, int TotalTicks) CalculateTicks(
            double startSeconds,
            double endSeconds,
            int frameRate)
        {
            if (startSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(startSeconds));
            if (endSeconds < startSeconds)
                throw new ArgumentOutOfRangeException(nameof(endSeconds));
            if (frameRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(frameRate));

            var startTick = checked((int)Math.Floor(startSeconds * frameRate));
            var endTick = checked((int)Math.Ceiling(endSeconds * frameRate));
            return (startTick, endTick - startTick);
        }
    }
}
