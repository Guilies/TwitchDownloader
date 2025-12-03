using System;
using System.IO;
using SkiaSharp;
using TwitchDownloaderCore.Models;

namespace TwitchDownloaderCore.Options
{
    public enum AspectRatio
    {
        SixteenByNine,  // 16:9
        FourByThree     // 4:3
    }

    public enum OutputResolution
    {
        UHD_4K,      // 3840x2160 (16:9) or 2880x2160 (4:3)
        QHD_1440p,   // 2560x1440 (16:9) or 1920x1440 (4:3)
        FHD_1080p,   // 1920x1080 (16:9) or 1440x1080 (4:3)
        HD_720p,     // 1280x720 (16:9) or 960x720 (4:3)
        SD_480p,     // 854x480 (16:9) or 640x480 (4:3)
        SD_360p      // 640x360 (16:9) or 480x360 (4:3)
    }

    public class CombinedRenderOptions
    {
        // VOD Download Options
        public long Id { get; set; }
        public string Quality { get; set; }
        public string Oauth { get; set; }
        public int DownloadThreads { get; set; } = 4;
        public int ThrottleKib { get; set; } = -1;

        // Chat Download Options
        public bool EmbedChatData { get; set; } = true;
        public bool BttvEmotes { get; set; } = true;
        public bool FfzEmotes { get; set; } = true;
        public bool StvEmotes { get; set; } = true;

        // Trim Options (shared between VOD and Chat)
        public bool TrimBeginning { get; set; }
        public TimeSpan TrimBeginningTime { get; set; }
        public bool TrimEnding { get; set; }
        public TimeSpan TrimEndingTime { get; set; }
        public VideoTrimMode TrimMode { get; set; } = VideoTrimMode.Safe;

        // Chat Render Options
        public SKColor ChatBackgroundColor { get; set; } = SKColor.Parse("#111111");
        public SKColor ChatAlternateBackgroundColor { get; set; } = SKColor.Parse("#191919");
        public SKColor MessageColor { get; set; } = SKColor.Parse("#ffffff");
        public string Font { get; set; } = "Inter Embedded";
        public double FontSize { get; set; } = 24.0;
        public bool ShowTimestamps { get; set; } = true;
        public bool ShowBadges { get; set; } = true;
        public bool ShowUserAvatars { get; set; } = false;
        public bool AlternateMessageBackgrounds { get; set; } = false;
        public bool Outline { get; set; } = false;
        public double OutlineSize { get; set; } = 4.0;
        public int Framerate { get; set; } = 30;
        public double UpdateRate { get; set; } = 0.2;
        public bool SubMessages { get; set; } = true;
        public bool DisperseCommentOffsets { get; set; } = true;

        // Composite Options (NEW - Core of Combined Render)
        public AspectRatio OutputAspectRatio { get; set; } = AspectRatio.SixteenByNine;
        public OutputResolution OutputResolution { get; set; } = OutputResolution.FHD_1080p;
        public int ChatWidthUnits { get; set; } = 4; // Units in aspect ratio (e.g., 4 out of 16 for 16:9)

        // Computed Properties
        public int OutputWidth => GetOutputWidth();
        public int OutputHeight => GetOutputHeight();
        public int ChatWidth => CalculateChatWidth();
        public int ChatHeight => OutputHeight; // Chat ALWAYS uses full output height
        public int VodWidth => OutputWidth - ChatWidth;
        public double VodScalingFactor => CalculateVodScalingFactor();

        // Output Options
        public string OutputFile { get; set; }
        public string OutputFormat { get; set; } = "mp4";
        public string FfmpegPath { get; set; } = "ffmpeg";
        public string TempFolder { get; set; }

        // Callbacks
        public Func<FileInfo, FileInfo> FileCollisionCallback { get; set; } = info => info;
        public Func<DirectoryInfo[], DirectoryInfo[]> CacheCleanerCallback { get; set; } = _ => Array.Empty<DirectoryInfo>();

        // Helper Methods
        private int GetOutputWidth()
        {
            return (OutputResolution, OutputAspectRatio) switch
            {
                (OutputResolution.UHD_4K, AspectRatio.SixteenByNine) => 3840,
                (OutputResolution.UHD_4K, AspectRatio.FourByThree) => 2880,
                (OutputResolution.QHD_1440p, AspectRatio.SixteenByNine) => 2560,
                (OutputResolution.QHD_1440p, AspectRatio.FourByThree) => 1920,
                (OutputResolution.FHD_1080p, AspectRatio.SixteenByNine) => 1920,
                (OutputResolution.FHD_1080p, AspectRatio.FourByThree) => 1440,
                (OutputResolution.HD_720p, AspectRatio.SixteenByNine) => 1280,
                (OutputResolution.HD_720p, AspectRatio.FourByThree) => 960,
                (OutputResolution.SD_480p, AspectRatio.SixteenByNine) => 854,
                (OutputResolution.SD_480p, AspectRatio.FourByThree) => 640,
                (OutputResolution.SD_360p, AspectRatio.SixteenByNine) => 640,
                (OutputResolution.SD_360p, AspectRatio.FourByThree) => 480,
                _ => 1920
            };
        }

        private int GetOutputHeight()
        {
            return OutputResolution switch
            {
                OutputResolution.UHD_4K => 2160,
                OutputResolution.QHD_1440p => 1440,
                OutputResolution.FHD_1080p => 1080,
                OutputResolution.HD_720p => 720,
                OutputResolution.SD_480p => 480,
                OutputResolution.SD_360p => 360,
                _ => 1080
            };
        }

        private int CalculateChatWidth()
        {
            double aspectWidth = OutputAspectRatio == AspectRatio.SixteenByNine ? 16.0 : 4.0;
            int width = (int)(OutputWidth * (ChatWidthUnits / aspectWidth));
            // Ensure even number for H.264 encoding
            return (width % 2 == 0) ? width : width + 1;
        }

        private double CalculateVodScalingFactor()
        {
            double aspectWidth = OutputAspectRatio == AspectRatio.SixteenByNine ? 16.0 : 4.0;
            return 1.0 - (ChatWidthUnits / aspectWidth);
        }

        /// <summary>
        /// Validates the combined render options
        /// </summary>
        /// <returns>Null if valid, error message if invalid</returns>
        public string Validate()
        {
            if (Id <= 0)
                return "Invalid VOD ID";

            if (string.IsNullOrWhiteSpace(OutputFile))
                return "Output file path is required";

            // Validate chat width units based on aspect ratio
            int maxChatUnits = OutputAspectRatio == AspectRatio.SixteenByNine ? 15 : 3;
            if (ChatWidthUnits < 1 || ChatWidthUnits > maxChatUnits)
                return $"Chat width must be between 1 and {maxChatUnits} units for {OutputAspectRatio} aspect ratio";

            if (FontSize < 8 || FontSize > 48)
                return "Font size must be between 8 and 48";

            if (Framerate < 10 || Framerate > 120)
                return "Framerate must be between 10 and 120";

            if (UpdateRate < 0.1 || UpdateRate > 5.0)
                return "Update rate must be between 0.1 and 5.0 seconds";

            return null; // Valid
        }
    }
}
