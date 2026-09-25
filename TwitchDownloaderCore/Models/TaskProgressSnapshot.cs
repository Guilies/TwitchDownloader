using System;

namespace TwitchDownloaderCore.Models
{
    public sealed record TaskProgressSnapshot(
        string Phase,
        string Action,
        int PhaseIndex,
        int PhaseCount,
        int OverallPercent,
        int ActionPercent,
        TimeSpan Elapsed,
        TimeSpan? Remaining,
        string Detail);
}
