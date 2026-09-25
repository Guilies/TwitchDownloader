using System;
using System.Collections.Generic;
using TwitchDownloaderCore.Extensions;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Models.Interfaces;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Tools;

namespace TwitchDownloaderCore
{
    internal readonly record struct ResolvedVideoQuality(
        string Name,
        Resolution Resolution,
        VideoFrameRate FrameRate,
        M3U8.Stream Playlist);

    internal readonly record struct ResolvedRenderTimeline(
        TimeSpan Start,
        TimeSpan End,
        bool TrimBeginning,
        bool TrimEnding)
    {
        internal TimeSpan Duration => End - Start;
    }

    /// <summary>
    /// Pure combined-render calculations kept separate from network and process
    /// orchestration so correctness can be validated with deterministic fixtures.
    /// </summary>
    internal static class CombinedRenderPlanner
    {
        internal static ResolvedVideoQuality ResolveVideoQuality(
            IVideoQualities<M3U8.Stream> qualities,
            string requestedQuality)
        {
            ArgumentNullException.ThrowIfNull(qualities);

            var quality = qualities.GetQuality(requestedQuality) ?? qualities.BestQuality();
            if (quality is null)
                throw new InvalidOperationException("The VOD playlist does not contain a usable video quality.");
            if (quality.Item.IsAudioOnly())
                throw new InvalidOperationException($"The selected quality '{quality.Name}' does not contain video.");
            if (quality.Resolution.Width == 0 || quality.Resolution.Height == 0)
                throw new InvalidOperationException($"The selected quality '{quality.Name}' does not contain video dimensions.");

            var frameRate = quality.Framerate > 0 ? quality.Framerate : 30m;
            return new ResolvedVideoQuality(
                quality.Name,
                quality.Resolution,
                VideoFrameRate.FromDecimal(frameRate),
                quality.Item);
        }

        internal static VideoScalingInfo CalculateScaling(
            Resolution sourceResolution,
            int outputWidth,
            int outputHeight,
            int chatWidth,
            int chatHeight)
        {
            if (sourceResolution.Width == 0 || sourceResolution.Height == 0)
                throw new ArgumentOutOfRangeException(nameof(sourceResolution));
            if (outputWidth <= 0 || outputHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(outputWidth));
            if (chatWidth <= 0 || chatWidth >= outputWidth || chatHeight != outputHeight)
                throw new ArgumentOutOfRangeException(nameof(chatWidth));

            var vodCellWidth = outputWidth - chatWidth;
            var widthScale = vodCellWidth / (double)sourceResolution.Width;
            var heightScale = outputHeight / (double)sourceResolution.Height;
            var scale = Math.Min(widthScale, heightScale);

            var scaledWidth = MakeEvenAtLeastTwo((int)Math.Floor(sourceResolution.Width * scale));
            var scaledHeight = MakeEvenAtLeastTwo((int)Math.Floor(sourceResolution.Height * scale));

            var scalingInfo = new VideoScalingInfo
            {
                SourceVodWidth = checked((int)sourceResolution.Width),
                SourceVodHeight = checked((int)sourceResolution.Height),
                ScaledVodWidth = Math.Min(scaledWidth, vodCellWidth),
                ScaledVodHeight = Math.Min(scaledHeight, outputHeight),
                VodCellWidth = vodCellWidth,
                ChatWidth = chatWidth,
                ChatHeight = chatHeight,
                OutputWidth = outputWidth,
                OutputHeight = outputHeight,
                VodScalingFactor = scale
            };

            scalingInfo.CalculatePadding();
            return scalingInfo;
        }

        internal static string BuildFilterComplex(VideoScalingInfo scaling, VideoFrameRate frameRate)
        {
            ArgumentNullException.ThrowIfNull(scaling);

            var fps = frameRate.FfmpegExpression;
            return $"[0:v]scale={scaling.ScaledVodWidth}:{scaling.ScaledVodHeight}:flags=lanczos," +
                   $"pad={scaling.VodCellWidth}:{scaling.OutputHeight}:{scaling.VodPaddingLeft}:{scaling.LetterboxPaddingTop}:black," +
                   $"setsar=1,fps={fps}[vod];" +
                   $"[1:v]setsar=1,fps={fps}[chat];" +
                   "[vod][chat]hstack=inputs=2[outv]";
        }

        internal static ResolvedRenderTimeline ResolveTimeline(
            CombinedRenderOptions options,
            TimeSpan vodDuration)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (vodDuration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(vodDuration));

            var start = options.TrimBeginning ? options.TrimBeginningTime : TimeSpan.Zero;
            var end = options.TrimEnding ? options.TrimEndingTime : vodDuration;

            if (start < TimeSpan.Zero || start >= vodDuration)
                throw new ArgumentOutOfRangeException(nameof(options.TrimBeginningTime));
            if (end <= start || end > vodDuration)
                throw new ArgumentOutOfRangeException(nameof(options.TrimEndingTime));

            return new ResolvedRenderTimeline(start, end, options.TrimBeginning, options.TrimEnding);
        }

        internal static List<string> BuildCompositeArguments(
            string vodPath,
            string chatVideoPath,
            string filterComplex,
            string outputPath,
            int ffmpegThreads)
        {
            return new List<string>
            {
                "-y",
                "-filter_complex_threads", ffmpegThreads.ToString(),
                "-i", vodPath,
                "-i", chatVideoPath,
                "-filter_complex", filterComplex,
                "-map", "[outv]",
                "-map", "0:a?",
                "-c:v", "libx264",
                "-preset", "medium",
                "-crf", "23",
                "-threads", ffmpegThreads.ToString(),
                "-pix_fmt", "yuv420p",
                "-c:a", "copy",
                "-shortest",
                outputPath
            };
        }

        internal static List<string> BuildSinglePassCompositeArguments(
            string vodPath,
            int chatWidth,
            int chatHeight,
            int chatFrameRate,
            string chatPixelFormat,
            string filterComplex,
            string outputPath,
            ResolvedCombinedEncoder encoder,
            int ffmpegThreads)
        {
            if (chatWidth <= 0 || chatHeight <= 0 || chatFrameRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(chatWidth));
            if (string.IsNullOrWhiteSpace(chatPixelFormat))
                throw new ArgumentException("A raw chat pixel format is required.", nameof(chatPixelFormat));
            ArgumentNullException.ThrowIfNull(encoder);

            var arguments = new List<string>
            {
                "-y",
                "-progress", "pipe:2",
                "-nostats",
                "-filter_complex_threads", ffmpegThreads.ToString(),
                "-i", vodPath,
                "-thread_queue_size", "512",
                "-f", "rawvideo",
                "-framerate", chatFrameRate.ToString(),
                "-pix_fmt", chatPixelFormat,
                "-video_size", $"{chatWidth}x{chatHeight}",
                "-i", "pipe:0",
                "-filter_complex", filterComplex,
                "-map", "[outv]",
                "-map", "0:a?"
            };

            arguments.AddRange(encoder.Arguments);
            if (!encoder.IsHardware)
            {
                arguments.Add("-threads");
                arguments.Add(ffmpegThreads.ToString());
            }
            arguments.AddRange(new[]
            {
                "-pix_fmt", "yuv420p",
                "-c:a", "copy",
                "-shortest",
                outputPath
            });
            return arguments;
        }

        private static int MakeEvenAtLeastTwo(int value) => Math.Max(2, value / 2 * 2);
    }
}
