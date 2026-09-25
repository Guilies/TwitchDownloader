using System;

namespace TwitchDownloaderCore.ChatRender.Core
{
    internal static class ChatRenderFrameRatePlanner
    {
        /// <summary>
        /// Finds the lowest integer frame rate that preserves the requested renderer's
        /// exact update grid. The final combined compositor restores the VOD frame rate.
        /// </summary>
        internal static int GetStaticFrameRate(int requestedFrameRate, double updateRate)
        {
            if (requestedFrameRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(requestedFrameRate));
            if (!double.IsFinite(updateRate) || updateRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(updateRate));

            int requestedUpdateFrame = Math.Max(1, (int)(updateRate * requestedFrameRate));
            return requestedFrameRate / GreatestCommonDivisor(requestedFrameRate, requestedUpdateFrame);
        }

        private static int GreatestCommonDivisor(int left, int right)
        {
            while (right != 0)
            {
                int remainder = left % right;
                left = right;
                right = remainder;
            }

            return Math.Abs(left);
        }
    }
}
