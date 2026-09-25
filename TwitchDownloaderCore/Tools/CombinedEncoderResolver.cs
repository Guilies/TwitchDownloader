using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;

namespace TwitchDownloaderCore.Tools
{
    internal static class CombinedEncoderResolver
    {
        // Some NVENC generations reject very small frames even though the
        // encoder and driver are otherwise functional. Keep the smoke test at
        // a broadly supported size to avoid a false software fallback.
        internal const int HardwareProbeDimension = 256;

        private static readonly CombinedRenderEncoder[] AutoHardwareOrder =
        {
            CombinedRenderEncoder.NvidiaNvenc,
            CombinedRenderEncoder.IntelQuickSync,
            CombinedRenderEncoder.AmdAmf,
            CombinedRenderEncoder.AppleVideoToolbox
        };

        internal static async Task<ResolvedCombinedEncoder> ResolveAsync(
            string ffmpegPath,
            CombinedRenderEncoder requestedEncoder,
            CombinedRenderSpeedProfile profile,
            string workingDirectory,
            ITaskProgress progress,
            CancellationToken cancellationToken)
        {
            if (requestedEncoder == CombinedRenderEncoder.Software)
            {
                var software = ResolveFromAvailable(requestedEncoder, profile, new HashSet<string>());
                progress.LogInfo($"Using encoder {software.FfmpegEncoder} with the {profile} profile.");
                return software;
            }

            var available = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in GetCandidates(requestedEncoder))
            {
                string ffmpegEncoder = GetFfmpegEncoder(candidate);
                string logPath = Path.Combine(workingDirectory, $"ffmpeg_probe_{ffmpegEncoder}.log");
                try
                {
                    var candidateSettings = CreateSettings(
                        requestedEncoder,
                        candidate,
                        profile,
                        fellBack: false);
                    var probeArguments = BuildProbeArguments(candidateSettings);
                    await FfmpegRunner.RunAsync(
                        ffmpegPath,
                        probeArguments,
                        workingDirectory,
                        logPath,
                        expectedOutputPath: null,
                        progress,
                        cancellationToken);
                    available.Add(ffmpegEncoder);
                    if (requestedEncoder == CombinedRenderEncoder.AutoHardware)
                        break;
                }
                catch (FfmpegExecutionException)
                {
                    progress.LogVerbose($"FFmpeg encoder probe failed for {ffmpegEncoder}.");
                }
            }

            var resolved = ResolveFromAvailable(requestedEncoder, profile, available);
            if (resolved.FellBackToSoftware)
            {
                progress.LogWarning(
                    $"Requested encoder '{requestedEncoder}' is unavailable; falling back to libx264.");
            }
            else
            {
                progress.LogInfo(
                    $"Using encoder {resolved.FfmpegEncoder} with the {profile} profile.");
            }
            return resolved;
        }

        internal static List<string> BuildProbeArguments(ResolvedCombinedEncoder encoder)
        {
            ArgumentNullException.ThrowIfNull(encoder);

            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i",
                $"color=c=black:s={HardwareProbeDimension}x{HardwareProbeDimension}:d=0.1",
                "-frames:v", "1", "-an"
            };
            arguments.AddRange(encoder.Arguments);
            arguments.AddRange(new[] { "-pix_fmt", "yuv420p", "-f", "null", "-" });
            return arguments;
        }

        internal static ResolvedCombinedEncoder ResolveFromAvailable(
            CombinedRenderEncoder requestedEncoder,
            CombinedRenderSpeedProfile profile,
            IReadOnlySet<string> availableFfmpegEncoders)
        {
            CombinedRenderEncoder resolvedEncoder = requestedEncoder switch
            {
                CombinedRenderEncoder.Software => CombinedRenderEncoder.Software,
                CombinedRenderEncoder.AutoHardware => FindFirstAvailable(availableFfmpegEncoders),
                _ when availableFfmpegEncoders.Contains(GetFfmpegEncoder(requestedEncoder)) => requestedEncoder,
                _ => CombinedRenderEncoder.Software
            };

            bool fellBack = requestedEncoder != CombinedRenderEncoder.Software &&
                            resolvedEncoder == CombinedRenderEncoder.Software;
            return CreateSettings(requestedEncoder, resolvedEncoder, profile, fellBack);
        }

        private static CombinedRenderEncoder FindFirstAvailable(IReadOnlySet<string> available)
        {
            foreach (var encoder in AutoHardwareOrder)
            {
                if (available.Contains(GetFfmpegEncoder(encoder)))
                    return encoder;
            }
            return CombinedRenderEncoder.Software;
        }

        private static IEnumerable<CombinedRenderEncoder> GetCandidates(CombinedRenderEncoder requested)
        {
            if (requested != CombinedRenderEncoder.AutoHardware)
                return new[] { requested };

            return OperatingSystem.IsMacOS()
                ? new[] { CombinedRenderEncoder.AppleVideoToolbox }
                : new[]
                {
                    CombinedRenderEncoder.NvidiaNvenc,
                    CombinedRenderEncoder.IntelQuickSync,
                    CombinedRenderEncoder.AmdAmf
                };
        }

        private static string GetFfmpegEncoder(CombinedRenderEncoder encoder) => encoder switch
        {
            CombinedRenderEncoder.Software => "libx264",
            CombinedRenderEncoder.NvidiaNvenc => "h264_nvenc",
            CombinedRenderEncoder.IntelQuickSync => "h264_qsv",
            CombinedRenderEncoder.AmdAmf => "h264_amf",
            CombinedRenderEncoder.AppleVideoToolbox => "h264_videotoolbox",
            _ => throw new ArgumentOutOfRangeException(nameof(encoder))
        };

        private static ResolvedCombinedEncoder CreateSettings(
            CombinedRenderEncoder requested,
            CombinedRenderEncoder resolved,
            CombinedRenderSpeedProfile profile,
            bool fellBack)
        {
            var arguments = resolved switch
            {
                CombinedRenderEncoder.Software => SoftwareArguments(profile),
                CombinedRenderEncoder.NvidiaNvenc => NvidiaArguments(profile),
                CombinedRenderEncoder.IntelQuickSync => QuickSyncArguments(profile),
                CombinedRenderEncoder.AmdAmf => AmdArguments(profile),
                CombinedRenderEncoder.AppleVideoToolbox => VideoToolboxArguments(profile),
                _ => throw new ArgumentOutOfRangeException(nameof(resolved))
            };

            return new ResolvedCombinedEncoder(
                requested,
                resolved,
                profile,
                GetFfmpegEncoder(resolved),
                arguments,
                resolved != CombinedRenderEncoder.Software,
                fellBack);
        }

        private static IReadOnlyList<string> SoftwareArguments(CombinedRenderSpeedProfile profile) => profile switch
        {
            CombinedRenderSpeedProfile.Fast => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "24" },
            CombinedRenderSpeedProfile.Balanced => new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "23" },
            CombinedRenderSpeedProfile.Quality => new[] { "-c:v", "libx264", "-preset", "slow", "-crf", "20" },
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };

        private static IReadOnlyList<string> NvidiaArguments(CombinedRenderSpeedProfile profile) => profile switch
        {
            CombinedRenderSpeedProfile.Fast => new[] { "-c:v", "h264_nvenc", "-preset", "p3", "-rc", "vbr", "-cq", "25", "-b:v", "0" },
            CombinedRenderSpeedProfile.Balanced => new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "23", "-b:v", "0" },
            CombinedRenderSpeedProfile.Quality => new[] { "-c:v", "h264_nvenc", "-preset", "p7", "-rc", "vbr", "-cq", "20", "-b:v", "0" },
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };

        private static IReadOnlyList<string> QuickSyncArguments(CombinedRenderSpeedProfile profile) => profile switch
        {
            CombinedRenderSpeedProfile.Fast => new[] { "-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "25" },
            CombinedRenderSpeedProfile.Balanced => new[] { "-c:v", "h264_qsv", "-preset", "medium", "-global_quality", "23" },
            CombinedRenderSpeedProfile.Quality => new[] { "-c:v", "h264_qsv", "-preset", "slow", "-global_quality", "20" },
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };

        private static IReadOnlyList<string> AmdArguments(CombinedRenderSpeedProfile profile)
        {
            var (quality, qp) = profile switch
            {
                CombinedRenderSpeedProfile.Fast => ("speed", "25"),
                CombinedRenderSpeedProfile.Balanced => ("balanced", "23"),
                CombinedRenderSpeedProfile.Quality => ("quality", "20"),
                _ => throw new ArgumentOutOfRangeException(nameof(profile))
            };
            return new[] { "-c:v", "h264_amf", "-quality", quality, "-rc", "cqp", "-qp_i", qp, "-qp_p", qp };
        }

        private static IReadOnlyList<string> VideoToolboxArguments(CombinedRenderSpeedProfile profile) => profile switch
        {
            CombinedRenderSpeedProfile.Fast => new[] { "-c:v", "h264_videotoolbox", "-q:v", "55" },
            CombinedRenderSpeedProfile.Balanced => new[] { "-c:v", "h264_videotoolbox", "-q:v", "65" },
            CombinedRenderSpeedProfile.Quality => new[] { "-c:v", "h264_videotoolbox", "-q:v", "75" },
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
    }
}
