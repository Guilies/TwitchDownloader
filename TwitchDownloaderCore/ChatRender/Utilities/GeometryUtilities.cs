using System.Collections.Generic;
using System.Linq;
using NeoSmart.Unicode;

namespace TwitchDownloaderCore.ChatRender.Utilities
{
    public static class GeometryUtilities
    {
        public static string GetKeyName(IEnumerable<Codepoint> codepoints)
        {
            var codepointList = from codepoint in codepoints where codepoint.Value != 0xFE0F select codepoint.Value.ToString("X");

            return string.Join(' ', codepointList);
        }
    }
}
