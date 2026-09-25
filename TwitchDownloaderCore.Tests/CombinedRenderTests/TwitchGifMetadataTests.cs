using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Tests.Fixtures;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Tests.CombinedRenderTests
{
    public sealed class TwitchGifMetadataTests
    {
        // Captured from a public VOD on 2026-09-25. The replay contains only this label;
        // message(id: ...) returns GifContent. Tracking query parameters are omitted here.
        private const string NativeId = "2df06102-5b90-43ae-8934-8de31d3386ae";
        private const string Label = "[marching band happy dance GIF by UCF Marching Knights]";
        private const string GifUrl = "https://media3.giphy.com/media/ef4syBUGUtRUzzOTtk/giphy.gif";

        [Fact]
        public async Task RecoversNativeGifFromReplayMessageIdWithoutGuessingFromLabel()
        {
            var comment = CreateComment(NativeId, Label);
            using var client = Client((request, _) =>
            {
                Assert.Contains(NativeId, request);
                Assert.Contains("gifID:id", request);
                return Response(new { data = new { m0 = NativeMessage(NativeId) } });
            });
            await TwitchGifMetadata.EnrichAsync(new() { comment }, StubTaskProgress.Instance, CancellationToken.None, client);
            var fragment = Assert.Single(comment.message.fragments);
            Assert.Equal("ef4syBUGUtRUzzOTtk", fragment.gif.id);
            Assert.Equal(GifUrl, fragment.gif.url);
            Assert.Equal(Label, fragment.text);
            Assert.Equal(Label, comment.message.body);
        }

        [Fact]
        public async Task NormalAndUnavailableMessagesAreUnchangedEvenWithGifLikeLabels()
        {
            var ordinary = CreateComment("ordinary", "[a GIF label]");
            var missing = CreateComment("missing", Label);
            var original = ordinary.message.fragments;
            using var client = Client((_, _) => Response(new
            {
                data = new
                {
                    m0 = new { id = "ordinary", content = new { fragments = new[] { new { text = "[a GIF label]", content = (object?)null } } } },
                    m1 = (object?)null
                }
            }));
            await TwitchGifMetadata.EnrichAsync(new() { ordinary, missing }, StubTaskProgress.Instance, CancellationToken.None, client);
            Assert.Same(original, ordinary.message.fragments);
            Assert.Null(Assert.Single(ordinary.message.fragments).gif);
            Assert.Equal(Label, missing.message.body);
        }

        [Fact]
        public async Task BatchesAndDeduplicatesIdsAndDoesNotRefetchKnownGifs()
        {
            var comments = Enumerable.Range(0, TwitchGifMetadata.BatchSize + 1).Select(i => CreateComment(i.ToString(), "text")).ToList();
            comments.Add(comments[0].Clone());
            comments.Add(GifChatFixture.Create().comments[0]);
            int calls = 0;
            using var client = Client((query, _) =>
            {
                Interlocked.Increment(ref calls);
                var ids = Regex.Matches(query, "m(?<alias>\\d+):message\\(id:\"(?<id>[^\"]+)\"\\)");
                Assert.InRange(ids.Count, 1, TwitchGifMetadata.BatchSize);
                var data = ids.ToDictionary(m => "m" + m.Groups["alias"].Value, m => NativeMessage(m.Groups["id"].Value));
                return Response(new { data });
            });
            await TwitchGifMetadata.EnrichAsync(comments, StubTaskProgress.Instance, CancellationToken.None, client);
            Assert.Equal(2, calls);
            Assert.All(comments, c => Assert.NotNull(Assert.Single(c.message.fragments).gif));
        }

        [Fact]
        public async Task MismatchedResponseCannotAttachGifToAnotherMessage()
        {
            var comment = CreateComment("expected", Label);
            using var client = Client((_, _) => Response(new { data = new { m0 = NativeMessage("other") } }));
            await TwitchGifMetadata.EnrichAsync(new() { comment }, StubTaskProgress.Instance, CancellationToken.None, client);
            Assert.Null(comment.message.fragments[0].gif);
        }

        [Fact]
        public async Task PartialGraphqlErrorsStillPreserveSuccessfulMetadata()
        {
            var comment = CreateComment(NativeId, Label);
            using var client = Client((_, _) => Response(new
            {
                data = new { m0 = NativeMessage(NativeId) },
                errors = new[] { new { message = "temporarily unavailable" } }
            }));
            await TwitchGifMetadata.EnrichAsync(new() { comment }, StubTaskProgress.Instance, CancellationToken.None, client);
            Assert.NotNull(comment.message.fragments[0].gif);
        }

        [Fact]
        public async Task RateLimitRetriesAreBoundedAndCanRecover()
        {
            int calls = 0;
            using var client = Client((_, _) =>
            {
                if (++calls == 1)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                    return Task.FromResult(response);
                }
                return Response(new { data = new { m0 = NativeMessage(NativeId) } });
            });
            var comment = CreateComment(NativeId, Label);
            await TwitchGifMetadata.EnrichAsync(new() { comment }, StubTaskProgress.Instance, CancellationToken.None, client);
            Assert.Equal(2, calls);
            Assert.NotNull(comment.message.fragments[0].gif);
        }

        [Fact]
        public async Task FailedLookupKeepsLabelAndStopsFurtherBatches()
        {
            int calls = 0;
            using var client = Client((_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            });
            var comments = Enumerable.Range(0, 200).Select(i => CreateComment(i.ToString(), Label)).ToList();
            await TwitchGifMetadata.EnrichAsync(comments, StubTaskProgress.Instance, CancellationToken.None, client);
            Assert.InRange(calls, 1, 2);
            Assert.All(comments, c => Assert.Equal(Label, c.message.body));
        }

        [Fact]
        public async Task CallerCancellationIsNotTreatedAsMissingMetadata()
        {
            using var cancellation = new CancellationTokenSource();
            using var client = Client(async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TwitchGifMetadata.EnrichAsync(
                new() { CreateComment(NativeId, Label) }, StubTaskProgress.Instance, cancellation.Token, client));
        }

        private static object NativeMessage(string id) => new
        {
            id, content = new { fragments = new[] { new { text = Label, content = new { __typename = "GifContent", gifID = "ef4syBUGUtRUzzOTtk", url = GifUrl } } } }
        };

        private static Comment CreateComment(string id, string label)
        {
            var comment = CombinedRenderFixtureFactory.CreateChatRoot(0, 1).comments[0];
            comment._id = id;
            comment.message.body = label;
            comment.message.fragments = new() { new() { text = label } };
            return comment;
        }

        private static Task<HttpResponseMessage> Response(object value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json")
        });

        private static HttpClient Client(Func<string, CancellationToken, Task<HttpResponseMessage>> handler) => new(new Handler(handler));

        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<string, CancellationToken, Task<HttpResponseMessage>> _handler;
            public Handler(Func<string, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                return await _handler(body.RootElement.GetProperty("query").GetString()!, token);
            }
        }
    }
}
