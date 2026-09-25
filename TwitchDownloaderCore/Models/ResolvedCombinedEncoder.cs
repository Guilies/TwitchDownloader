using System.Collections.Generic;
using TwitchDownloaderCore.Options;

namespace TwitchDownloaderCore.Models
{
    internal sealed record ResolvedCombinedEncoder(
        CombinedRenderEncoder RequestedEncoder,
        CombinedRenderEncoder Encoder,
        CombinedRenderSpeedProfile Profile,
        string FfmpegEncoder,
        IReadOnlyList<string> Arguments,
        bool IsHardware,
        bool FellBackToSoftware);
}
