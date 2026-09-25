using System;

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

        // Width reserved for the VOD in the final side-by-side layout.
        public int VodCellWidth { get; set; }

        // Chat dimensions (always full output height)
        public int ChatWidth { get; set; }
        public int ChatHeight { get; set; }

        // Final output dimensions
        public int OutputWidth { get; set; }
        public int OutputHeight { get; set; }

        // Letterboxing information
        public bool RequiresLetterboxing => ScaledVodHeight < OutputHeight;
        public bool RequiresPillarboxing => ScaledVodWidth < VodCellWidth;
        public bool RequiresPadding => RequiresLetterboxing || RequiresPillarboxing;
        public int LetterboxPaddingTop { get; set; }
        public int LetterboxPaddingBottom { get; set; }
        public int TotalLetterboxPadding => LetterboxPaddingTop + LetterboxPaddingBottom;
        public int VodPaddingLeft { get; set; }
        public int VodPaddingRight { get; set; }

        // Scaling factors
        public double VodScalingFactor { get; set; }
        public double WidthScalingFactor => (double)ScaledVodWidth / SourceVodWidth;
        public double HeightScalingFactor => (double)ScaledVodHeight / SourceVodHeight;

        /// <summary>
        /// Calculates letterbox padding to center the VOD vertically
        /// Also ensures all dimensions are even numbers for H.264 encoding
        /// </summary>
        public void CalculatePadding()
        {
            // CRITICAL: Ensure all dimensions are even for H.264 encoding
            ScaledVodWidth = (ScaledVodWidth / 2) * 2;
            ScaledVodHeight = (ScaledVodHeight / 2) * 2;
            ChatWidth = (ChatWidth / 2) * 2;
            ChatHeight = (ChatHeight / 2) * 2;
            OutputWidth = (OutputWidth / 2) * 2;
            OutputHeight = (OutputHeight / 2) * 2;
            VodCellWidth = (VodCellWidth / 2) * 2;

            int verticalPadding = Math.Max(0, OutputHeight - ScaledVodHeight);
            LetterboxPaddingTop = verticalPadding / 2;
            LetterboxPaddingBottom = verticalPadding - LetterboxPaddingTop;

            int horizontalPadding = Math.Max(0, VodCellWidth - ScaledVodWidth);
            VodPaddingLeft = horizontalPadding / 2;
            VodPaddingRight = horizontalPadding - VodPaddingLeft;
        }

        public void CalculateLetterboxPadding() => CalculatePadding();

        /// <summary>
        /// Returns a summary string for debugging and logging
        /// </summary>
        public override string ToString()
        {
            return $"VideoScalingInfo: Source={SourceVodWidth}x{SourceVodHeight}, " +
                   $"Scaled VOD={ScaledVodWidth}x{ScaledVodHeight}, " +
                   $"Chat={ChatWidth}x{ChatHeight}, " +
                   $"Output={OutputWidth}x{OutputHeight}, " +
                   $"Padding=L{VodPaddingLeft}/R{VodPaddingRight}/T{LetterboxPaddingTop}/B{LetterboxPaddingBottom}, " +
                   $"VodScale={VodScalingFactor:F3}";
        }
    }
}
