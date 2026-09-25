using TwitchDownloaderCore.Models;

namespace TwitchDownloaderCore.Interfaces
{
    public interface IDetailedTaskProgress
    {
        void ReportDetailedProgress(TaskProgressSnapshot snapshot);
    }
}
