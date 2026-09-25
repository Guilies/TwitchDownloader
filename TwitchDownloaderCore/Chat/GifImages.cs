using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Chat
{
    internal static class GifImages
    {
        internal static async Task<Dictionary<string, TwitchEmote>> FetchAsync(
            ChatRoot chat, string cacheDirectory, ITaskLogger logger, bool offline,
            CancellationToken cancellationToken, bool useEmbedded = true)
        {
            var result = new Dictionary<string, TwitchEmote>(StringComparer.Ordinal);
            var references = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var comment in chat.comments)
            {
                foreach (var fragment in comment.message?.fragments ?? Enumerable.Empty<Fragment>())
                {
                    if (fragment.gif?.TryGetAsset(out var key, out var url) == true)
                        references.TryAdd(key, url);
                }
            }

            var embeds = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (useEmbedded && chat.embeddedData?.gifs != null)
            {
                foreach (var embed in chat.embeddedData.gifs)
                {
                    if (embed.id != null && references.ContainsKey(embed.id))
                        embeds.TryAdd(embed.id, embed.data);
                }
            }

            try
            {
                foreach (var (key, url) in references)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (embeds.TryGetValue(key, out var embeddedBytes))
                    {
                        try
                        {
                            result.Add(key, Decode(embeddedBytes, key));
                            continue;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogVerbose($"Unable to decode embedded GIF {key}: {ex.Message}");
                        }
                    }

                    try
                    {
                        var (bytes, codec) = await TwitchHelper.GetImage(
                            new DirectoryInfo(Path.Combine(cacheDirectory, "gifs")),
                            url, key, 1, "gif", offline, logger, cancellationToken);
                        using (codec)
                        {
                            if (bytes != null)
                                result.Add(key, Decode(bytes, key));
                            else
                                logger.LogVerbose($"GIF {key} is unavailable offline; using its text label.");
                        }
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        // A removed GIF, corrupt response or timeout must not abort a video render.
                        logger.LogWarning($"Unable to load GIF {key}; using its text label. {ex.Message}");
                    }
                }
                return result;
            }
            catch
            {
                foreach (var image in result.Values)
                    image.Dispose();
                throw;
            }
        }

        private static TwitchEmote Decode(byte[] bytes, string key)
        {
            if (bytes == null || bytes.Length == 0)
                throw new InvalidDataException("Empty GIF image.");

            var codec = SKCodec.Create(new MemoryStream(bytes));
            if (codec == null)
                throw new InvalidDataException("Unsupported GIF image.");

            // GIFs are larger than emotes. Bound decoded memory before allocating every frame.
            if ((codec.EncodedFormat != SKEncodedImageFormat.Gif && codec.EncodedFormat != SKEncodedImageFormat.Webp) ||
                (long)codec.Info.Width * codec.Info.Height * Math.Max(1, codec.FrameCount) > 64_000_000)
            {
                codec.Dispose();
                throw new InvalidDataException("Unsupported GIF format or decoded size exceeds the limit.");
            }

            try
            {
                return new TwitchEmote(bytes, codec, EmoteProvider.FirstParty, 1, key, "GIF");
            }
            catch
            {
                codec.Dispose();
                throw;
            }
        }

        internal static async Task EmbedAsync(ChatRoot chat, string cacheDirectory,
            ITaskLogger logger, CancellationToken cancellationToken, bool replace = false)
        {
            var images = await FetchAsync(chat, cacheDirectory, logger, false, cancellationToken, !replace);
            try
            {
                chat.embeddedData ??= new EmbeddedData();
                chat.embeddedData.gifs = images.Select(pair => new EmbedEmoteData
                {
                    id = pair.Key,
                    data = pair.Value.ImageData,
                    imageScale = 1,
                    width = pair.Value.Width,
                    height = pair.Value.Height
                }).ToList();
            }
            finally
            {
                foreach (var image in images.Values)
                    image.Dispose();
            }
        }
    }
}
