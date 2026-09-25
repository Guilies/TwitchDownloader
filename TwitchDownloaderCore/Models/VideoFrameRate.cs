using System;
using System.Globalization;

namespace TwitchDownloaderCore.Models
{
    /// <summary>
    /// A video frame rate represented as a rational value so rates such as
    /// 30000/1001 are not rounded to an integer before being passed to FFmpeg.
    /// </summary>
    public readonly record struct VideoFrameRate
    {
        public uint Numerator { get; }
        public uint Denominator { get; }

        public decimal FramesPerSecond => (decimal)Numerator / Denominator;

        public string FfmpegExpression => string.Create(
            CultureInfo.InvariantCulture,
            $"{Numerator}/{Denominator}");

        public VideoFrameRate(uint numerator, uint denominator)
        {
            if (numerator == 0)
                throw new ArgumentOutOfRangeException(nameof(numerator));
            if (denominator == 0)
                throw new ArgumentOutOfRangeException(nameof(denominator));

            var divisor = GreatestCommonDivisor(numerator, denominator);
            Numerator = numerator / divisor;
            Denominator = denominator / divisor;
        }

        public static VideoFrameRate FromDecimal(decimal frameRate)
        {
            if (frameRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(frameRate));

            // M3U8 commonly reports rounded NTSC rates. Preserve their canonical
            // rational form rather than turning 29.97/59.94 into 30/60.
            if (Math.Abs(frameRate - 23.976m) <= 0.001m)
                return new VideoFrameRate(24_000, 1_001);
            if (Math.Abs(frameRate - 29.97m) <= 0.001m)
                return new VideoFrameRate(30_000, 1_001);
            if (Math.Abs(frameRate - 59.94m) <= 0.001m)
                return new VideoFrameRate(60_000, 1_001);

            var bits = decimal.GetBits(frameRate);
            var scale = (bits[3] >> 16) & 0x7F;
            var denominator = 1u;
            for (var i = 0; i < scale; i++)
            {
                denominator = checked(denominator * 10);
            }

            var numerator = checked((uint)(frameRate * denominator));
            return new VideoFrameRate(numerator, denominator);
        }

        public override string ToString() => FfmpegExpression;

        private static uint GreatestCommonDivisor(uint left, uint right)
        {
            while (right != 0)
            {
                var remainder = left % right;
                left = right;
                right = remainder;
            }

            return left;
        }
    }
}
