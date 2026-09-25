using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Chat
{
    /// <summary>
    /// VOD comment fragments omit GIFs. The corresponding Message, addressed by the
    /// same chat ID, retains GifContent. Enrich only messages that actually contain
    /// GIFs; leave other replay data untouched.
    /// </summary>
    internal static class TwitchGifMetadata
    {
        internal const int BatchSize = 50;
        private static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestHeaders = { { "Client-ID", "kd1unb4b3q4t58fwlpcbzcbnm76a8fp" } }
        };

        internal static async Task EnrichAsync(List<Comment> comments, ITaskProgress progress,
            CancellationToken cancellationToken, HttpClient client = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groups = comments.Where(c => !string.IsNullOrEmpty(c._id) && c.message != null &&
                    c.message.fragments?.Any(f => f.gif != null) != true)
                .GroupBy(c => c._id, StringComparer.Ordinal).ToArray();
            if (groups.Length == 0)
                return;

            progress.SetStatus("Fetching GIF Metadata");
            progress.ReportProgress(0);
            int completed = 0, unavailable = 0, found = 0;
            await Parallel.ForEachAsync(groups.Chunk(BatchSize), new ParallelOptions
            {
                MaxDegreeOfParallelism = 2,
                CancellationToken = cancellationToken
            }, async (batch, token) =>
            {
                if (Volatile.Read(ref unavailable) != 0)
                    return;
                try
                {
                    var ids = batch.Select(group => group.Key).ToArray();
                    var response = await FetchAsync(client ?? Client, ids, token);
                    if (response.errors?.Length > 0)
                    {
                        // Partial GraphQL data is still useful, but stop sending further batches
                        // if the schema/auth policy has changed. Existing labels remain intact.
                        if (Interlocked.Exchange(ref unavailable, 1) == 0)
                            progress.LogWarning("Some GIF metadata is unavailable from Twitch; retaining chat labels.");
                    }
                    for (int i = 0; i < batch.Length; i++)
                    {
                        if (response.data?.TryGetValue("m" + i, out var message) == true &&
                            message?.id == ids[i])
                        {
                            foreach (var comment in batch[i])
                                if (Apply(comment, message))
                                    Interlocked.Increment(ref found);
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException &&
                                           !cancellationToken.IsCancellationRequested)
                {
                    if (Interlocked.Exchange(ref unavailable, 1) == 0)
                        progress.LogWarning($"GIF metadata lookup failed; retaining chat labels. {ex.Message}");
                }
                finally
                {
                    var count = Interlocked.Add(ref completed, batch.Length);
                    progress.ReportProgress(count * 100 / groups.Length);
                }
            });
            cancellationToken.ThrowIfCancellationRequested();
            progress.LogVerbose($"Recovered GIF metadata for {found} chat messages.");
        }

        private static async Task<LookupResponse> FetchAsync(HttpClient client, string[] ids, CancellationToken token)
        {
            var query = new StringBuilder("query ChatGifMetadata {");
            for (int i = 0; i < ids.Length; i++)
            {
                query.Append('m').Append(i).Append(":message(id:").Append(JsonSerializer.Serialize(ids[i]))
                    .Append("){id content{fragments{text content{__typename ... on GifContent{gifID:id url} ... on Emote{emoteID:id}}}}}");
            }
            query.Append('}');

            for (int attempt = 0; ; attempt++)
            {
                using var response = await client.PostAsJsonAsync("https://gql.twitch.tv/gql",
                    new { query = query.ToString() }, token);
                if (attempt < 2 && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt + 1);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 0, 10)), token);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<LookupResponse>(cancellationToken: token)
                    ?? throw new JsonException("Empty GIF metadata response.");
            }
        }

        private static bool Apply(Comment comment, NativeMessage message)
        {
            var source = message.content?.fragments;
            if (source?.Any(f => f.content?.__typename == "GifContent" && !string.IsNullOrEmpty(f.content.url)) != true)
                return false;

            var fragments = new List<Fragment>(source.Length);
            var seenGifs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fragment in source)
            {
                var content = fragment.content;
                ChatGif gif = null;
                Emoticon emote = null;
                if (content?.__typename == "GifContent")
                {
                    // Twitch's client also deduplicates repeated GIF particles in a message.
                    if (!seenGifs.Add(content.gifID ?? content.url ?? string.Empty))
                        continue;
                    gif = new ChatGif { id = content.gifID, url = content.url };
                }
                else if (content?.__typename == "Emote" && !string.IsNullOrEmpty(content.emoteID))
                    emote = new Emoticon { emoticon_id = content.emoteID };

                fragments.Add(new Fragment { text = fragment.text ?? (gif != null ? "[GIF]" : ""), gif = gif, emoticon = emote });
            }

            comment.message.fragments = fragments;
            comment.message.body = string.Concat(fragments.Select(f => f.text));
            return true;
        }

        private sealed class LookupResponse
        {
            public Dictionary<string, NativeMessage> data { get; set; }
            public JsonElement[] errors { get; set; }
        }

        private sealed class NativeMessage
        {
            public string id { get; set; }
            public NativeContent content { get; set; }
        }

        private sealed class NativeContent
        {
            public NativeFragment[] fragments { get; set; }
        }

        private sealed class NativeFragment
        {
            public string text { get; set; }
            public NativeMedia content { get; set; }
        }

        private sealed class NativeMedia
        {
            public string __typename { get; set; }
            public string gifID { get; set; }
            public string url { get; set; }
            public string emoteID { get; set; }
        }
    }
}
