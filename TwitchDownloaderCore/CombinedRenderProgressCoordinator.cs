using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Tools;

namespace TwitchDownloaderCore
{
    internal enum CombinedRenderProgressComponent
    {
        Metadata,
        VodDownload,
        ChatDownload,
        ChatPreparation,
        Composition
    }

    internal enum CombinedRenderProgressPhase
    {
        Metadata,
        Acquisition,
        Composition
    }

    internal sealed class CombinedRenderProgressCoordinator
    {
        private static readonly IReadOnlyDictionary<CombinedRenderProgressComponent, double> ComponentWeights =
            new Dictionary<CombinedRenderProgressComponent, double>
            {
                [CombinedRenderProgressComponent.Metadata] = 5,
                [CombinedRenderProgressComponent.VodDownload] = 40,
                [CombinedRenderProgressComponent.ChatDownload] = 15,
                [CombinedRenderProgressComponent.ChatPreparation] = 15,
                [CombinedRenderProgressComponent.Composition] = 25
            };

        private readonly object _sync = new();
        private readonly ITaskProgress _parent;
        private readonly Dictionary<CombinedRenderProgressComponent, int> _percentages = new();
        private readonly Stopwatch _phaseStopwatch = new();
        private CombinedRenderProgressPhase _phase;
        private string _action = "Starting";
        private string _compositionDetail = "Waiting for FFmpeg";
        private TimeSpan? _compositionRemaining;
        private int _lastOverallPercent;
        private TaskProgressSnapshot _lastSnapshot;

        internal CombinedRenderProgressCoordinator(ITaskProgress parent)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            foreach (var component in Enum.GetValues<CombinedRenderProgressComponent>())
                _percentages[component] = 0;
        }

        internal void BeginPhase(CombinedRenderProgressPhase phase, string action)
        {
            lock (_sync)
            {
                _phase = phase;
                _action = action;
                _phaseStopwatch.Restart();
                TaskProgressSnapshot snapshot = CreateSnapshot();
                _lastSnapshot = snapshot;
                Emit(snapshot, phaseChanged: true);
            }
        }

        internal ITaskProgress CreateComponentProgress(
            CombinedRenderProgressComponent component,
            string taskName,
            IReadOnlyList<double> stageWeights = null)
        {
            return new ComponentTaskProgress(this, component, taskName, stageWeights);
        }

        internal void ReportComponent(CombinedRenderProgressComponent component, int percent)
        {
            lock (_sync)
            {
                int boundedPercent = Math.Clamp(percent, 0, 100);
                if (boundedPercent <= _percentages[component])
                    return;

                _percentages[component] = boundedPercent;
                TaskProgressSnapshot snapshot = CreateSnapshot();
                if (snapshot == _lastSnapshot)
                    return;
                _lastSnapshot = snapshot;
                Emit(snapshot, phaseChanged: false);
            }
        }

        internal void CompleteComponent(CombinedRenderProgressComponent component) =>
            ReportComponent(component, 100);

        internal void ReportComposition(FfmpegProgressSnapshot ffmpegProgress)
        {
            ArgumentNullException.ThrowIfNull(ffmpegProgress);

            string speed = ffmpegProgress.Speed.HasValue
                ? $"{ffmpegProgress.Speed.Value:F2}x"
                : "speed pending";
            string frame = ffmpegProgress.Frame.HasValue
                ? $"frame {ffmpegProgress.Frame.Value.ToString("N0", CultureInfo.CurrentCulture)}"
                : "frame pending";

            lock (_sync)
            {
                _compositionDetail =
                    $"{ffmpegProgress.OutputTime:c} / {ffmpegProgress.Duration:c} • {speed} • {frame}";
                _compositionRemaining = ffmpegProgress.Remaining;
                _percentages[CombinedRenderProgressComponent.Composition] = Math.Max(
                    _percentages[CombinedRenderProgressComponent.Composition],
                    ffmpegProgress.Percent);
                TaskProgressSnapshot snapshot = CreateSnapshot();
                _lastSnapshot = snapshot;
                Emit(snapshot, phaseChanged: false);
            }
        }

        private TaskProgressSnapshot CreateSnapshot()
        {
            int calculatedOverall = (int)Math.Round(
                ComponentWeights.Sum(pair => pair.Value * _percentages[pair.Key] / 100d));
            _lastOverallPercent = Math.Max(_lastOverallPercent, calculatedOverall);

            var (phaseName, phaseIndex, actionPercent, detail, remaining) = _phase switch
            {
                CombinedRenderProgressPhase.Metadata => (
                    "Metadata",
                    1,
                    _percentages[CombinedRenderProgressComponent.Metadata],
                    "Resolving media, timeline, layout, and encoder",
                    (TimeSpan?)null),
                CombinedRenderProgressPhase.Acquisition => (
                    "Acquisition",
                    2,
                    CalculateAcquisitionPercent(),
                    CreateAcquisitionDetail(),
                    (TimeSpan?)null),
                CombinedRenderProgressPhase.Composition => (
                    "Composition",
                    3,
                    _percentages[CombinedRenderProgressComponent.Composition],
                    _compositionDetail,
                    _compositionRemaining),
                _ => throw new ArgumentOutOfRangeException()
            };

            return new TaskProgressSnapshot(
                phaseName,
                _action,
                phaseIndex,
                3,
                Math.Clamp(_lastOverallPercent, 0, 100),
                Math.Clamp(actionPercent, 0, 100),
                _phaseStopwatch.Elapsed,
                remaining,
                detail);
        }

        private int CalculateAcquisitionPercent()
        {
            const double acquisitionWeight = 70;
            double completed =
                ComponentWeights[CombinedRenderProgressComponent.VodDownload] *
                    _percentages[CombinedRenderProgressComponent.VodDownload] / 100d +
                ComponentWeights[CombinedRenderProgressComponent.ChatDownload] *
                    _percentages[CombinedRenderProgressComponent.ChatDownload] / 100d +
                ComponentWeights[CombinedRenderProgressComponent.ChatPreparation] *
                    _percentages[CombinedRenderProgressComponent.ChatPreparation] / 100d;
            return (int)Math.Round(completed / acquisitionWeight * 100d);
        }

        private string CreateAcquisitionDetail() =>
            $"VOD {_percentages[CombinedRenderProgressComponent.VodDownload]}% • " +
            $"Chat {_percentages[CombinedRenderProgressComponent.ChatDownload]}% • " +
            $"Assets {_percentages[CombinedRenderProgressComponent.ChatPreparation]}%";

        private void Emit(TaskProgressSnapshot snapshot, bool phaseChanged)
        {
            if (_parent is IDetailedTaskProgress detailedProgress)
            {
                detailedProgress.ReportDetailedProgress(snapshot);
                return;
            }

            if (phaseChanged)
            {
                _parent.SetTemplateStatus(
                    $"{snapshot.Action} {{0}}% [{snapshot.PhaseIndex}/{snapshot.PhaseCount}]",
                    snapshot.OverallPercent);
            }
            else
            {
                _parent.ReportProgress(snapshot.OverallPercent);
            }
        }

        private sealed class ComponentTaskProgress : ITaskProgress
        {
            private readonly CombinedRenderProgressCoordinator _coordinator;
            private readonly CombinedRenderProgressComponent _component;
            private readonly string _taskName;
            private readonly double[] _stageWeights;
            private int _stage = -1;
            private double _completedStageWeight;

            internal ComponentTaskProgress(
                CombinedRenderProgressCoordinator coordinator,
                CombinedRenderProgressComponent component,
                string taskName,
                IReadOnlyList<double> stageWeights)
            {
                _coordinator = coordinator;
                _component = component;
                _taskName = taskName;
                _stageWeights = stageWeights is null ? null : stageWeights.ToArray();
                if (_stageWeights is not null &&
                    (Math.Abs(_stageWeights.Sum() - 1d) > 0.0001 || _stageWeights.Any(x => x <= 0)))
                {
                    throw new ArgumentException("Stage weights must be positive and sum to one.", nameof(stageWeights));
                }
            }

            public void SetStatus(string status)
            {
                BeginStage();
                _coordinator._parent.LogInfo($"[{_taskName}] {status}");
            }

            public void SetTemplateStatus(string status, int initialPercent)
            {
                BeginStage();
                _coordinator._parent.LogInfo($"[{_taskName}] {string.Format(status, initialPercent)}");
                ReportProgress(initialPercent);
            }

            public void SetTemplateStatus(
                string status,
                int initialPercent,
                TimeSpan initialTime1,
                TimeSpan initialTime2)
            {
                BeginStage();
                _coordinator._parent.LogInfo(
                    $"[{_taskName}] {string.Format(status, initialPercent, initialTime1, initialTime2)}");
                ReportProgress(initialPercent, initialTime1, initialTime2);
            }

            public void ReportProgress(int percent)
            {
                int mapped = MapStagePercent(percent);
                _coordinator.ReportComponent(_component, mapped);
            }

            public void ReportProgress(int percent, TimeSpan time1, TimeSpan time2) =>
                ReportProgress(percent);

            private void BeginStage()
            {
                if (_stageWeights is null)
                    return;

                if (_stage >= 0)
                    _completedStageWeight += _stageWeights[_stage];
                if (_stage + 1 < _stageWeights.Length)
                    _stage++;

                _coordinator.ReportComponent(
                    _component,
                    (int)Math.Round(_completedStageWeight * 100d));
            }

            private int MapStagePercent(int percent)
            {
                if (_stageWeights is null)
                    return Math.Clamp(percent, 0, 100);
                if (_stage < 0)
                    _stage = 0;

                double mapped = _completedStageWeight +
                    _stageWeights[_stage] * Math.Clamp(percent, 0, 100) / 100d;
                return (int)Math.Round(mapped * 100d);
            }

            public void LogVerbose(string logMessage) => _coordinator._parent.LogVerbose(logMessage);
            public void LogVerbose(DefaultInterpolatedStringHandler logMessage) => _coordinator._parent.LogVerbose(logMessage);
            public void LogInfo(string logMessage) => _coordinator._parent.LogInfo(logMessage);
            public void LogInfo(DefaultInterpolatedStringHandler logMessage) => _coordinator._parent.LogInfo(logMessage);
            public void LogWarning(string logMessage) => _coordinator._parent.LogWarning(logMessage);
            public void LogWarning(DefaultInterpolatedStringHandler logMessage) => _coordinator._parent.LogWarning(logMessage);
            public void LogError(string logMessage) => _coordinator._parent.LogError(logMessage);
            public void LogError(DefaultInterpolatedStringHandler logMessage) => _coordinator._parent.LogError(logMessage);
            public void LogFfmpeg(string logMessage) => _coordinator._parent.LogFfmpeg(logMessage);
        }
    }
}
