namespace TwitchDownloaderCore.Models
{
    /// <summary>
    /// Contains calculated scaling information for combining VOD and chat videos
    /// </summary>
    public class VideoScalingInfo
    {
        // Source VOD dimensions
        public int SourceVodWidth { get; set; }
        public int SourceVodHeight { get; set; }
        public double SourceVodAspectRatio => (double)SourceVodWidth / SourceVodHeight;

        // Scaled VOD dimensions (to fit alongside chat)
        public int ScaledVodWidth { get; set; }
        public int ScaledVodHeight { get; set; }

        // Chat dimensions (always full output height)
        public int ChatWidth { get; set; }
        public int ChatHeight { get; set; }

        // Final output dimensions
        public int OutputWidth { get; set; }
        public int OutputHeight { get; set; }

        // Letterboxing information
        public bool RequiresLetterboxing => ScaledVodHeight < OutputHeight;
        public int LetterboxPaddingTop { get; set; }
        public int LetterboxPaddingBottom { get; set; }
        public int TotalLetterboxPadding => LetterboxPaddingTop + LetterboxPaddingBottom;

        // Scaling factors
        public double VodScalingFactor { get; set; }
        public double WidthScalingFactor => (double)ScaledVodWidth / SourceVodWidth;
        public double HeightScalingFactor => (double)ScaledVodHeight / SourceVodHeight;

        /// <summary>
        /// Calculates letterbox padding to center the VOD vertically
        /// Also ensures all dimensions are even numbers for H.264 encoding
        /// </summary>
        public void CalculateLetterboxPadding()
        {
            // CRITICAL: Ensure all dimensions are even for H.264 encoding
            ScaledVodWidth = (ScaledVodWidth / 2) * 2;
            ScaledVodHeight = (ScaledVodHeight / 2) * 2;
            ChatWidth = (ChatWidth / 2) * 2;
            ChatHeight = (ChatHeight / 2) * 2;
            OutputWidth = (OutputWidth / 2) * 2;
            OutputHeight = (OutputHeight / 2) * 2;
            
            if (ScaledVodHeight >= OutputHeight)
            {
                LetterboxPaddingTop = 0;
                LetterboxPaddingBottom = 0;
                return;
            }

            int totalPadding = OutputHeight - ScaledVodHeight;
            LetterboxPaddingTop = totalPadding / 2;
            LetterboxPaddingBottom = totalPadding - LetterboxPaddingTop;
        }

        /// <summary>
        /// Returns a summary string for debugging and logging
        /// </summary>
        public override string ToString()
        {
            return $"VideoScalingInfo: Source={SourceVodWidth}x{SourceVodHeight}, " +
                   $"Scaled VOD={ScaledVodWidth}x{ScaledVodHeight}, " +
                   $"Chat={ChatWidth}x{ChatHeight}, " +
                   $"Output={OutputWidth}x{OutputHeight}, " +
                   $"Letterbox={LetterboxPaddingTop}/{LetterboxPaddingBottom}, " +
                   $"VodScale={VodScalingFactor:F3}";
        }
    }
}
