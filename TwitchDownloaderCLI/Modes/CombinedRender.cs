using SkiaSharp;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using TwitchDownloaderCLI.Modes.Arguments;
using TwitchDownloaderCLI.Tools;
using TwitchDownloaderCore;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Tools;

namespace TwitchDownloaderCLI.Modes
{
    internal static class CombinedRender
    {
        internal static void Render(CombinedRenderArgs inputOptions)
        {
            using var progress = new CliTaskProgress(inputOptions.LogLevel);

            FfmpegHandler.DetectFfmpeg(inputOptions.FfmpegPath, progress);

            var collisionHandler = new FileCollisionHandler(inputOptions, progress);
            var renderOptions = GetRenderOptions(inputOptions, collisionHandler, progress);

            using var combinedRenderer = new CombinedRenderer(renderOptions, progress);
            combinedRenderer.RenderAsync(new CancellationToken()).Wait();
        }

        private static CombinedRenderOptions GetRenderOptions(CombinedRenderArgs inputOptions, FileCollisionHandler collisionHandler, ITaskLogger logger)
        {
            // Parse VOD ID from input
            long vodId;
            var vodIdMatch = IdParse.MatchVideoId(inputOptions.Id);
            if (vodIdMatch is { Success: true } && long.TryParse(vodIdMatch.ValueSpan, out vodId))
            {
                // Valid VOD ID
            }
            else
            {
                throw new ArgumentException("Invalid VOD ID or URL");
            }

            // Parse aspect ratio
            var aspectRatio = inputOptions.AspectRatio.ToLower() switch
            {
                "16:9" or "16by9" or "sixteen-by-nine" => AspectRatio.SixteenByNine,
                "4:3" or "4by3" or "four-by-three" => AspectRatio.FourByThree,
                _ => throw new NotSupportedException("Invalid aspect ratio. Valid values are: 16:9 or 4:3")
            };

            // Parse resolution
            var resolution = inputOptions.Resolution.ToLower() switch
            {
                "4k" or "uhd" or "2160p" => OutputResolution.UHD_4K,
                "1440p" or "qhd" => OutputResolution.QHD_1440p,
                "1080p" or "fhd" => OutputResolution.FHD_1080p,
                "720p" or "hd" => OutputResolution.HD_720p,
                "480p" or "sd" => OutputResolution.SD_480p,
                "360p" => OutputResolution.SD_360p,
                _ => throw new NotSupportedException("Invalid resolution. Valid values are: 4K, 1440p, 1080p, 720p, 480p, 360p")
            };

            var renderOptions = new CombinedRenderOptions
            {
                // VOD Download Options
                Id = vodId,
                Quality = inputOptions.Quality,
                Oauth = inputOptions.Oauth,
                DownloadThreads = inputOptions.DownloadThreads,
                ThrottleKib = inputOptions.ThrottleKib,

                // Trim Options
                TrimBeginning = ((TimeSpan)inputOptions.TrimBeginningTime).TotalSeconds >= 0,
                TrimBeginningTime = ((TimeSpan)inputOptions.TrimBeginningTime).TotalSeconds >= 0 
                    ? (TimeSpan)inputOptions.TrimBeginningTime 
                    : TimeSpan.Zero,
                TrimEnding = ((TimeSpan)inputOptions.TrimEndingTime).TotalSeconds >= 0,
                TrimEndingTime = ((TimeSpan)inputOptions.TrimEndingTime).TotalSeconds >= 0 
                    ? (TimeSpan)inputOptions.TrimEndingTime 
                    : TimeSpan.Zero,

                // Output Settings
                OutputAspectRatio = aspectRatio,
                OutputResolution = resolution,
                ChatWidthUnits = inputOptions.ChatWidthUnits,

                // Chat Render Options
                ChatBackgroundColor = SKColor.Parse(inputOptions.BackgroundColor),
                ChatAlternateBackgroundColor = SKColor.Parse(inputOptions.AlternateBackgroundColor),
                MessageColor = SKColor.Parse(inputOptions.MessageColor),
                Font = inputOptions.Font,
                FontSize = inputOptions.FontSize,
                ShowTimestamps = inputOptions.ShowTimestamps,
                ShowBadges = inputOptions.ShowBadges,
                ShowUserAvatars = inputOptions.ShowUserAvatars,
                AlternateMessageBackgrounds = inputOptions.AlternateMessageBackgrounds,
                Outline = inputOptions.Outline,
                OutlineSize = inputOptions.OutlineSize,
                Framerate = inputOptions.Framerate,
                UpdateRate = inputOptions.UpdateRate,
                SubMessages = inputOptions.SubMessages,
                DisperseCommentOffsets = inputOptions.DisperseCommentOffsets,

                // Emote Options
                BttvEmotes = inputOptions.BttvEmotes,
                FfzEmotes = inputOptions.FfzEmotes,
                StvEmotes = inputOptions.StvEmotes,
                EmbedChatData = inputOptions.EmbedChatData,

                // Output Options
                OutputFile = inputOptions.OutputFile,
                FfmpegPath = string.IsNullOrWhiteSpace(inputOptions.FfmpegPath) 
                    ? FfmpegHandler.FfmpegExecutableName 
                    : Path.GetFullPath(inputOptions.FfmpegPath),
                FfmpegThreads = inputOptions.FfmpegThreads,
                TempFolder = inputOptions.TempFolder,

                // Callbacks
                FileCollisionCallback = collisionHandler.HandleCollisionCallback,
            };

            // Validate chat width units
            int maxChatUnits = aspectRatio == AspectRatio.SixteenByNine ? 15 : 3;
            if (renderOptions.ChatWidthUnits < 1 || renderOptions.ChatWidthUnits > maxChatUnits)
            {
                throw new ArgumentException($"Chat width must be between 1 and {maxChatUnits} units for {aspectRatio} aspect ratio");
            }

            // Ensure even dimensions for H.264 encoding
            if (renderOptions.ChatHeight % 2 != 0 || renderOptions.ChatWidth % 2 != 0)
            {
                logger.LogWarning("Chat dimensions must be even for H.264 encoding, rounding to nearest even number");
            }

            logger.LogInfo($"Combined Render Configuration:");
            logger.LogInfo($"  VOD ID: {renderOptions.Id}");
            logger.LogInfo($"  Output Resolution: {renderOptions.OutputWidth}x{renderOptions.OutputHeight}");
            logger.LogInfo($"  Aspect Ratio: {aspectRatio}");
            logger.LogInfo($"  Chat Width: {renderOptions.ChatWidth}px ({renderOptions.ChatWidthUnits} units)");
            logger.LogInfo($"  Chat Height: {renderOptions.ChatHeight}px");
            logger.LogInfo($"  VOD Scaling Factor: {renderOptions.VodScalingFactor:P1}");

            return renderOptions;
        }
    }
}
