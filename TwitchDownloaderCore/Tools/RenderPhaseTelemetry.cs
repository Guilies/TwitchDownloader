using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TwitchDownloaderCore.Interfaces;

namespace TwitchDownloaderCore.Tools
{
    /// <summary>
    /// Lightweight phase telemetry for establishing render baselines without a
    /// dependency on an external metrics system.
    /// </summary>
    internal sealed class RenderPhaseTelemetry : IDisposable
    {
        private readonly string _phase;
        private readonly ITaskProgress _progress;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly long _allocatedBytes = GC.GetTotalAllocatedBytes(false);
        private readonly int _gen0Collections = GC.CollectionCount(0);
        private readonly int _gen1Collections = GC.CollectionCount(1);
        private readonly int _gen2Collections = GC.CollectionCount(2);
        private readonly TimeSpan _processorTime;
        private readonly long _workingSetBytes;
        private readonly string[] _trackedPaths;
        private readonly long _trackedBytes;
        private bool _disposed;

        internal RenderPhaseTelemetry(string phase, ITaskProgress progress, params string[] trackedPaths)
        {
            _phase = phase;
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _trackedPaths = trackedPaths?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray()
                ?? Array.Empty<string>();
            _trackedBytes = GetTrackedBytes(_trackedPaths);

            using var process = Process.GetCurrentProcess();
            _processorTime = process.TotalProcessorTime;
            _workingSetBytes = process.WorkingSet64;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _stopwatch.Stop();

            using var process = Process.GetCurrentProcess();
            var allocatedDelta = GC.GetTotalAllocatedBytes(false) - _allocatedBytes;
            var processorTimeDelta = process.TotalProcessorTime - _processorTime;
            var trackedBytes = GetTrackedBytes(_trackedPaths);
            _progress.LogInfo(
                $"[RenderMetrics] phase=\"{_phase}\" elapsed_ms={_stopwatch.Elapsed.TotalMilliseconds:F0} " +
                $"cpu_ms={processorTimeDelta.TotalMilliseconds:F0} allocated_bytes={allocatedDelta} " +
                $"gc0={GC.CollectionCount(0) - _gen0Collections} " +
                $"gc1={GC.CollectionCount(1) - _gen1Collections} gc2={GC.CollectionCount(2) - _gen2Collections} " +
                $"working_set_bytes={process.WorkingSet64} working_set_delta_bytes={process.WorkingSet64 - _workingSetBytes} " +
                $"peak_working_set_bytes={process.PeakWorkingSet64} tracked_bytes={trackedBytes} " +
                $"tracked_bytes_delta={trackedBytes - _trackedBytes}");
        }

        private static long GetTrackedBytes(string[] paths)
        {
            long totalBytes = 0;
            foreach (var path in paths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        totalBytes += new FileInfo(path).Length;
                        continue;
                    }

                    if (!Directory.Exists(path))
                        continue;

                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        totalBytes += new FileInfo(file).Length;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Telemetry must never fail the render.
                }
            }

            return totalBytes;
        }
    }
}
