using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using TwitchDownloaderCore.Interfaces;

namespace TwitchDownloaderCore.Tools
{
    internal sealed class FfmpegExecutionException : Exception
    {
        internal int? ExitCode { get; }
        internal string LogPath { get; }

        internal FfmpegExecutionException(string message, int? exitCode, string logPath)
            : base(message)
        {
            ExitCode = exitCode;
            LogPath = logPath;
        }
    }

    /// <summary>
    /// Runs FFmpeg with continuously drained output, deterministic cancellation,
    /// exit-code validation, and optional output-file validation.
    /// </summary>
    internal static class FfmpegRunner
    {
        internal static async Task<string> GetVersionAsync(
            string executable,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(executable))
                throw new ArgumentException("An FFmpeg executable is required.", nameof(executable));

            using var process = new Process
            {
                StartInfo =
                {
                    FileName = executable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("-version");

            if (!process.Start())
                throw new InvalidOperationException("FFmpeg did not start.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"FFmpeg version check failed with exit code {process.ExitCode}: {stderr}");

            using var reader = new StringReader(stdout);
            return await reader.ReadLineAsync() ?? "unknown";
        }

        internal static async Task RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            string logPath,
            string expectedOutputPath,
            ITaskProgress progress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(executable))
                throw new ArgumentException("An FFmpeg executable is required.", nameof(executable));
            ArgumentNullException.ThrowIfNull(arguments);
            if (string.IsNullOrWhiteSpace(workingDirectory))
                throw new ArgumentException("A working directory is required.", nameof(workingDirectory));
            if (string.IsNullOrWhiteSpace(logPath))
                throw new ArgumentException("A log path is required.", nameof(logPath));
            ArgumentNullException.ThrowIfNull(progress);

            Directory.CreateDirectory(workingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? workingDirectory);

            using var process = new Process
            {
                StartInfo =
                {
                    FileName = executable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = workingDirectory
                }
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            progress.LogVerbose($"Running FFmpeg: {executable} {string.Join(" ", arguments)}");

            if (!process.Start())
            {
                throw new FfmpegExecutionException("FFmpeg did not start.", null, logPath);
            }

            await using var logWriter = new StreamWriter(logPath, append: false);
            var stdoutTask = DrainAsync(process.StandardOutput, null);
            var stderrTask = DrainAsync(process.StandardError, async line =>
            {
                await logWriter.WriteLineAsync(line);
                progress.LogFfmpeg(line);
            });

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdoutTask, stderrTask);
                throw;
            }

            await Task.WhenAll(stdoutTask, stderrTask);
            await logWriter.FlushAsync();

            if (process.ExitCode != 0)
            {
                throw new FfmpegExecutionException(
                    $"FFmpeg failed with exit code {process.ExitCode}. See log at: {logPath}",
                    process.ExitCode,
                    logPath);
            }

            if (!string.IsNullOrWhiteSpace(expectedOutputPath))
            {
                var output = new FileInfo(expectedOutputPath);
                output.Refresh();
                if (!output.Exists || output.Length == 0)
                {
                    throw new FfmpegExecutionException(
                        $"FFmpeg exited successfully but did not create a non-empty output file: {expectedOutputPath}",
                        process.ExitCode,
                        logPath);
                }
            }
        }

        /// <summary>
        /// Runs FFmpeg while a producer writes a bounded raw input stream to stdin.
        /// Pipe writes provide backpressure; cancellation terminates the full process tree.
        /// </summary>
        internal static async Task RunWithInputAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            string logPath,
            string expectedOutputPath,
            ITaskProgress progress,
            Func<Stream, CancellationToken, Task> writeInputAsync,
            CancellationToken cancellationToken,
            TimeSpan? expectedDuration = null,
            int progressStart = 0,
            int progressEnd = 100,
            Action<FfmpegProgressSnapshot> detailedProgress = null)
        {
            if (string.IsNullOrWhiteSpace(executable))
                throw new ArgumentException("An FFmpeg executable is required.", nameof(executable));
            ArgumentNullException.ThrowIfNull(arguments);
            ArgumentNullException.ThrowIfNull(progress);
            ArgumentNullException.ThrowIfNull(writeInputAsync);
            if (progressStart is < 0 or > 100)
                throw new ArgumentOutOfRangeException(nameof(progressStart));
            if (progressEnd < progressStart || progressEnd > 100)
                throw new ArgumentOutOfRangeException(nameof(progressEnd));

            Directory.CreateDirectory(workingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? workingDirectory);

            using var process = new Process
            {
                StartInfo =
                {
                    FileName = executable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = workingDirectory
                }
            };
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);

            progress.LogVerbose($"Running FFmpeg with streamed input: {executable} {string.Join(" ", arguments)}");
            if (!process.Start())
                throw new FfmpegExecutionException("FFmpeg did not start.", null, logPath);

            await using var logWriter = new StreamWriter(logPath, append: false);
            var progressParser = expectedDuration.HasValue
                ? new FfmpegProgressParser(expectedDuration.Value)
                : null;
            var stdoutTask = DrainAsync(process.StandardOutput, null);
            var stderrTask = DrainAsync(process.StandardError, async line =>
            {
                await logWriter.WriteLineAsync(line);
                progress.LogFfmpeg(line);
                if (progressParser?.TryParse(line, out var ffmpegProgress) == true)
                {
                    int mappedPercent = progressStart +
                        (int)Math.Round(ffmpegProgress.Percent * (progressEnd - progressStart) / 100d);
                    progress.ReportProgress(mappedPercent);
                    detailedProgress?.Invoke(ffmpegProgress);
                }
            });

            Exception inputException = null;
            using var cancellationRegistration = cancellationToken.Register(
                static state => TryKillProcessTree((Process)state),
                process);
            try
            {
                await writeInputAsync(process.StandardInput.BaseStream, cancellationToken);
                await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                inputException = ex;
                TryKillProcessTree(process);
            }
            finally
            {
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdoutTask, stderrTask);
            await logWriter.FlushAsync();

            cancellationToken.ThrowIfCancellationRequested();

            if (inputException is not null and not IOException)
                ExceptionDispatchInfo.Capture(inputException).Throw();

            if (process.ExitCode != 0)
            {
                throw new FfmpegExecutionException(
                    $"FFmpeg failed with exit code {process.ExitCode}. See log at: {logPath}",
                    process.ExitCode,
                    logPath);
            }

            if (inputException is not null)
                ExceptionDispatchInfo.Capture(inputException).Throw();

            if (!string.IsNullOrWhiteSpace(expectedOutputPath))
            {
                var output = new FileInfo(expectedOutputPath);
                output.Refresh();
                if (!output.Exists || output.Length == 0)
                {
                    throw new FfmpegExecutionException(
                        $"FFmpeg exited successfully but did not create a non-empty output file: {expectedOutputPath}",
                        process.ExitCode,
                        logPath);
                }
            }
        }

        private static async Task DrainAsync(
            StreamReader reader,
            Func<string, Task> handleLine)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (handleLine is not null)
                    await handleLine(line);
            }
        }

        private static void TryKillProcessTree(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the HasExited check and Kill.
            }
        }
    }
}
