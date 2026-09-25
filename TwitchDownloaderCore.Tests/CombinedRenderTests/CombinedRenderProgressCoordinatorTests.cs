using System.Runtime.CompilerServices;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Tools;

namespace TwitchDownloaderCore.Tests.CombinedRenderTests
{
    public sealed class CombinedRenderProgressCoordinatorTests
    {
        [Fact]
        public void OverallProgressIsMonotonicWhileActionProgressResetsPerPhase()
        {
            var progress = new RecordingDetailedProgress();
            var coordinator = new CombinedRenderProgressCoordinator(progress);

            coordinator.BeginPhase(CombinedRenderProgressPhase.Metadata, "Preparing render");
            coordinator.CompleteComponent(CombinedRenderProgressComponent.Metadata);
            coordinator.BeginPhase(CombinedRenderProgressPhase.Acquisition, "Acquiring media");
            coordinator.ReportComponent(CombinedRenderProgressComponent.VodDownload, 50);
            coordinator.ReportComponent(CombinedRenderProgressComponent.ChatDownload, 100);
            coordinator.ReportComponent(CombinedRenderProgressComponent.ChatPreparation, 50);

            var acquisition = progress.Snapshots[^1];
            Assert.Equal(48, acquisition.OverallPercent);
            Assert.Equal(61, acquisition.ActionPercent);
            Assert.Equal("VOD 50% • Chat 100% • Assets 50%", acquisition.Detail);

            coordinator.ReportComponent(CombinedRenderProgressComponent.VodDownload, 40);
            Assert.Equal(acquisition, progress.Snapshots[^1]);

            coordinator.CompleteComponent(CombinedRenderProgressComponent.VodDownload);
            coordinator.CompleteComponent(CombinedRenderProgressComponent.ChatPreparation);
            Assert.Equal(75, progress.Snapshots[^1].OverallPercent);
            Assert.Equal(100, progress.Snapshots[^1].ActionPercent);

            coordinator.BeginPhase(CombinedRenderProgressPhase.Composition, "Compositing final video");
            Assert.Equal(75, progress.Snapshots[^1].OverallPercent);
            Assert.Equal(0, progress.Snapshots[^1].ActionPercent);

            coordinator.ReportComponent(CombinedRenderProgressComponent.Composition, 40);
            coordinator.ReportComponent(CombinedRenderProgressComponent.Composition, 20);
            Assert.Equal(85, progress.Snapshots[^1].OverallPercent);
            Assert.Equal(40, progress.Snapshots[^1].ActionPercent);

            coordinator.CompleteComponent(CombinedRenderProgressComponent.Composition);
            Assert.Equal(100, progress.Snapshots[^1].OverallPercent);
            Assert.Equal(100, progress.Snapshots[^1].ActionPercent);
            Assert.True(progress.Snapshots
                .Select(x => x.OverallPercent)
                .Zip(progress.Snapshots.Skip(1).Select(x => x.OverallPercent))
                .All(pair => pair.First <= pair.Second));
        }

        [Fact]
        public void StagedChildProgressMapsResetsIntoOneContinuousComponent()
        {
            var progress = new RecordingDetailedProgress();
            var coordinator = new CombinedRenderProgressCoordinator(progress);
            coordinator.BeginPhase(CombinedRenderProgressPhase.Metadata, "Preparing render");
            coordinator.CompleteComponent(CombinedRenderProgressComponent.Metadata);
            coordinator.BeginPhase(CombinedRenderProgressPhase.Acquisition, "Acquiring media");

            var vodProgress = coordinator.CreateComponentProgress(
                CombinedRenderProgressComponent.VodDownload,
                "VOD Download",
                new[] { 0.02, 0.83, 0.05, 0.10 });

            vodProgress.SetStatus("Fetching Video Info [1/4]");
            vodProgress.SetTemplateStatus("Downloading {0}% [2/4]", 0);
            vodProgress.ReportProgress(50);

            Assert.Contains("VOD 44%", progress.Snapshots[^1].Detail);

            vodProgress.SetTemplateStatus("Verifying Parts {0}% [3/4]", 0);
            Assert.Contains("VOD 85%", progress.Snapshots[^1].Detail);
            vodProgress.ReportProgress(100);
            vodProgress.SetTemplateStatus("Finalizing Video {0}% [4/4]", 0);
            Assert.Contains("VOD 90%", progress.Snapshots[^1].Detail);
        }

        [Fact]
        public void CompositionSnapshotIncludesFfmpegSpeedAndEta()
        {
            var progress = new RecordingDetailedProgress();
            var coordinator = new CombinedRenderProgressCoordinator(progress);
            coordinator.BeginPhase(CombinedRenderProgressPhase.Composition, "Compositing final video");

            coordinator.ReportComposition(new FfmpegProgressSnapshot(
                50,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(2),
                2.0,
                TimeSpan.FromSeconds(30),
                1_800,
                12_345_678));

            var snapshot = progress.Snapshots[^1];
            Assert.Equal(50, snapshot.ActionPercent);
            Assert.Equal(TimeSpan.FromSeconds(30), snapshot.Remaining);
            Assert.Contains("2.00x", snapshot.Detail);
            Assert.Contains(
                $"frame {1_800.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)}",
                snapshot.Detail);
        }

        [Fact]
        public void ConcurrentBranchReportsAreDeliveredInMonotonicOrder()
        {
            var progress = new RecordingDetailedProgress();
            var coordinator = new CombinedRenderProgressCoordinator(progress);
            coordinator.BeginPhase(CombinedRenderProgressPhase.Metadata, "Preparing render");
            coordinator.CompleteComponent(CombinedRenderProgressComponent.Metadata);
            coordinator.BeginPhase(CombinedRenderProgressPhase.Acquisition, "Acquiring media");

            Parallel.Invoke(
                () =>
                {
                    for (int percent = 1; percent <= 100; percent++)
                        coordinator.ReportComponent(CombinedRenderProgressComponent.VodDownload, percent);
                },
                () =>
                {
                    for (int percent = 1; percent <= 100; percent++)
                        coordinator.ReportComponent(CombinedRenderProgressComponent.ChatDownload, percent);
                },
                () =>
                {
                    for (int percent = 1; percent <= 100; percent++)
                        coordinator.ReportComponent(CombinedRenderProgressComponent.ChatPreparation, percent);
                });

            Assert.Equal(75, progress.Snapshots[^1].OverallPercent);
            Assert.Equal(100, progress.Snapshots[^1].ActionPercent);
            Assert.True(progress.Snapshots
                .Select(x => x.OverallPercent)
                .Zip(progress.Snapshots.Skip(1).Select(x => x.OverallPercent))
                .All(pair => pair.First <= pair.Second));
        }

        private sealed class RecordingDetailedProgress : ITaskProgress, IDetailedTaskProgress
        {
            internal List<TaskProgressSnapshot> Snapshots { get; } = new();

            public void ReportDetailedProgress(TaskProgressSnapshot snapshot) => Snapshots.Add(snapshot);
            public void SetStatus(string status) { }
            public void SetTemplateStatus(string status, int initialPercent) { }
            public void SetTemplateStatus(string status, int initialPercent, TimeSpan initialTime1, TimeSpan initialTime2) { }
            public void ReportProgress(int percent) { }
            public void ReportProgress(int percent, TimeSpan time1, TimeSpan time2) { }
            public void LogVerbose(string logMessage) { }
            public void LogVerbose(DefaultInterpolatedStringHandler logMessage) { }
            public void LogInfo(string logMessage) { }
            public void LogInfo(DefaultInterpolatedStringHandler logMessage) { }
            public void LogWarning(string logMessage) { }
            public void LogWarning(DefaultInterpolatedStringHandler logMessage) { }
            public void LogError(string logMessage) { }
            public void LogError(DefaultInterpolatedStringHandler logMessage) { }
            public void LogFfmpeg(string logMessage) { }
        }
    }
}
