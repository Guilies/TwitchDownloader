using System;
using System.Security.Cryptography;
using System.Text;

namespace TwitchDownloaderCore.TwitchObjects
{
    /// <summary>GIPHY metadata carried by a chat fragment; text remains its fallback label.</summary>
    public sealed class ChatGif
    {
        public string id { get; set; }
        public string url { get; set; }

        public ChatGif Clone() => new() { id = id, url = url };

        // Key by URL, not a user-supplied ID: renditions can differ and IDs must never become paths.
        internal bool TryGetAsset(out string key, out string assetUrl)
        {
            key = null;
            assetUrl = null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !(uri.Host.Equals("media.giphy.com", StringComparison.OrdinalIgnoreCase) ||
                  (uri.Host.Length == "media0.giphy.com".Length &&
                   uri.Host.StartsWith("media", StringComparison.OrdinalIgnoreCase) &&
                   uri.Host[5] is >= '0' and <= '9' &&
                   uri.Host.EndsWith(".giphy.com", StringComparison.OrdinalIgnoreCase))) ||
                !uri.AbsolutePath.StartsWith("/media/", StringComparison.Ordinal) ||
                !(uri.AbsolutePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
                  uri.AbsolutePath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            assetUrl = uri.GetLeftPart(UriPartial.Path) + uri.Query;
            key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assetUrl)));
            return true;
        }
    }
}
