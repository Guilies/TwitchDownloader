using SkiaSharp;
using TwitchDownloaderCore.Extensions;

namespace TwitchDownloaderCore.ChatRender.Utilities
{
    public static class ColorUtilities
    {
        public static SKColor AdjustUsernameVisibility(SKColor userColor, SKColor backgroundColor, bool outline, SKColor outlineColor)
        {
            const byte OPAQUE_THRESHOLD = byte.MaxValue / 2;
            if (!outline && backgroundColor.Alpha < OPAQUE_THRESHOLD)
            {
                // Background lightness cannot be truly known.
                return userColor;
            }

            var newUserColor = AdjustColorVisibility(userColor, outline ? outlineColor : backgroundColor);

            return outline || backgroundColor.Alpha == byte.MaxValue
            ? newUserColor
            : userColor.Lerp(newUserColor, (float)backgroundColor.Alpha / byte.MaxValue);
        }

        public static SKColor AdjustColorVisibility(SKColor foreground, SKColor background)
        {
            foreground.ToHsl(out var fgHue, out var fgSat, out var fgLight);
            background.ToHsl(out var bgHue, out var bgSat, out var bgLight);

            // Adjust lightness
            if (background.RelativeLuminance() > 0.5)
            {
                // Bright background
                if (fgLight > 60)
                {
                    fgLight = 60;
                }

                if (bgSat <= 28)
                {
                    fgHue = fgHue switch
                    {
                        > 48 and < 90 => AdjustHue(fgHue, 48, 90), // Yellow-Lime
                        > 164 and < 186 => AdjustHue(fgHue, 164, 186), // Turquoise
                        _ => fgHue
                    };
                }
            }
            else
            {
                // Dark background
                if (fgLight < 40)
                {
                    fgLight = 40;
                }

                if (bgSat <= 28)
                {
                    fgHue = fgHue switch
                    {
                        > 224 and < 263 => AdjustHue(fgHue, 224, 264), // Blue-Purple
                        _ => fgHue
                    };
                }
            }

            // Adjust hue on colored backgrounds
            if (bgSat > 28 && fgSat > 28)
            {
                var hueDiff = fgHue - bgHue;
                const int HUE_THRESHOLD = 25;
                if (System.Math.Abs(hueDiff) < HUE_THRESHOLD)
                {
                    var diffSign = hueDiff < 0 ? -1 : 1; // Math.Sign returns 1, -1, or 0. We only want 1 or -1.
                    fgHue = bgHue + HUE_THRESHOLD * diffSign;

                    if (fgHue < 0) fgHue += 360;
                    fgHue %= 360;
                }
            }

            return SKColor.FromHsl(fgHue, System.Math.Min(fgSat, 90), fgLight);
        }

        private static float AdjustHue(float hue, float lowerClamp, float upperClamp)
        {
            var midpoint = (upperClamp + lowerClamp) / 2;
            return hue >= midpoint ? upperClamp : lowerClamp;
        }
    }
}
