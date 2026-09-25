using System;
using System.Collections.Concurrent;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TwitchDownloaderCore.Interfaces;

namespace TwitchDownloaderCore.Tools
{
    internal static class RenderEnvironmentTelemetry
    {
        private static readonly ConcurrentDictionary<string, string> FfmpegVersions = new();

        internal static async Task LogAsync(
            ITaskProgress progress,
            string ffmpegPath,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);

            progress.LogInfo(
                $"[RenderEnvironment] os=\"{RuntimeInformation.OSDescription}\" " +
                $"os_arch={RuntimeInformation.OSArchitecture} process_arch={RuntimeInformation.ProcessArchitecture} " +
                $"framework=\"{RuntimeInformation.FrameworkDescription}\" processors={Environment.ProcessorCount} " +
                $"server_gc={GCSettings.IsServerGC} gc_latency={GCSettings.LatencyMode}");

            try
            {
                if (!FfmpegVersions.TryGetValue(ffmpegPath, out var version))
                {
                    version = await FfmpegRunner.GetVersionAsync(ffmpegPath, cancellationToken);
                    FfmpegVersions.TryAdd(ffmpegPath, version);
                }
                progress.LogInfo($"[RenderEnvironment] ffmpeg=\"{version}\"");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                progress.LogWarning($"Unable to record FFmpeg version: {ex.Message}");
            }
        }
    }
}
