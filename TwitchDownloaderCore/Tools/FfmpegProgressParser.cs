using System;
using System.Globalization;

namespace TwitchDownloaderCore.Tools
{
    internal sealed record FfmpegProgressSnapshot(
        int Percent,
        TimeSpan OutputTime,
        TimeSpan Duration,
        double? Speed,
        TimeSpan? Remaining,
        long? Frame,
        long? TotalSize);

    internal sealed class FfmpegProgressParser
    {
        private readonly long _durationMicroseconds;
        private long _elapsedMicroseconds;
        private double? _speed;
        private long? _frame;
        private long? _totalSize;
        private int _lastPercent;

        internal FfmpegProgressParser(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));

            _durationMicroseconds = duration.Ticks / 10;
        }

        internal bool TryParse(string line, out FfmpegProgressSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            if (TryParseMicroseconds(line, "out_time_us=", out long elapsedMicroseconds) ||
                TryParseMicroseconds(line, "out_time_ms=", out elapsedMicroseconds) ||
                TryParseTimestamp(line, out elapsedMicroseconds))
            {
                _elapsedMicroseconds = Math.Max(_elapsedMicroseconds, elapsedMicroseconds);
                return false;
            }

            if (TryParseLong(line, "frame=", out long frame))
            {
                _frame = frame;
                return false;
            }

            if (TryParseLong(line, "total_size=", out long totalSize))
            {
                _totalSize = totalSize;
                return false;
            }

            const string speedPrefix = "speed=";
            if (line.StartsWith(speedPrefix, StringComparison.Ordinal))
            {
                ReadOnlySpan<char> value = line.AsSpan(speedPrefix.Length).Trim();
                if (value.EndsWith("x", StringComparison.Ordinal))
                    value = value[..^1];
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed) &&
                    speed > 0)
                {
                    _speed = speed;
                }
                return false;
            }

            bool isEnd = line.Equals("progress=end", StringComparison.Ordinal);
            if (!isEnd && !line.Equals("progress=continue", StringComparison.Ordinal))
                return false;

            int parsed = isEnd
                ? 100
                : (int)Math.Clamp(_elapsedMicroseconds * 100L / _durationMicroseconds, 0L, 99L);
            _lastPercent = Math.Max(_lastPercent, parsed);

            var duration = TimeSpan.FromTicks(_durationMicroseconds * 10);
            var outputTime = TimeSpan.FromTicks(Math.Min(_elapsedMicroseconds, _durationMicroseconds) * 10);
            TimeSpan? remaining = null;
            if (isEnd)
            {
                remaining = TimeSpan.Zero;
            }
            else if (_speed.HasValue)
            {
                double remainingSeconds = Math.Max(0, (duration - outputTime).TotalSeconds) / _speed.Value;
                remaining = TimeSpan.FromSeconds(remainingSeconds);
            }

            snapshot = new FfmpegProgressSnapshot(
                _lastPercent,
                outputTime,
                duration,
                _speed,
                remaining,
                _frame,
                _totalSize);
            return true;
        }

        private static bool TryParseLong(string line, string prefix, out long value)
        {
            value = 0;
            return line.StartsWith(prefix, StringComparison.Ordinal) &&
                   long.TryParse(line.AsSpan(prefix.Length), NumberStyles.Integer,
                       CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseMicroseconds(string line, string prefix, out long value)
        {
            value = 0;
            return line.StartsWith(prefix, StringComparison.Ordinal) &&
                   long.TryParse(line.AsSpan(prefix.Length), NumberStyles.Integer,
                       CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseTimestamp(string line, out long microseconds)
        {
            const string prefix = "out_time=";
            microseconds = 0;
            if (!line.StartsWith(prefix, StringComparison.Ordinal) ||
                !TimeSpan.TryParse(line.AsSpan(prefix.Length), CultureInfo.InvariantCulture, out var elapsed))
            {
                return false;
            }

            microseconds = elapsed.Ticks / 10;
            return true;
        }
    }
}
