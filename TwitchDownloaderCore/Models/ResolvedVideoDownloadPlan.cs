using TwitchDownloaderCore.TwitchObjects.Gql;

namespace TwitchDownloaderCore.Models
{
    /// <summary>
    /// Immutable references to metadata and playlist selections shared by combined-render branches.
    /// </summary>
    internal sealed record ResolvedVideoDownloadPlan(
        GqlVideoResponse VideoInfoResponse,
        GqlVideoChapterResponse VideoChapterResponse,
        string[] AllQualityPaths,
        M3U8.Stream QualityPlaylist);
}
