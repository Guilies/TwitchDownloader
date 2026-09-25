using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Extensions;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Services;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;
using TwitchDownloaderCore.TwitchObjects.Gql;

namespace TwitchDownloaderCore
{
    public sealed class CombinedRenderer : IDisposable
    {
        private readonly CombinedRenderOptions _renderOptions;
        private readonly ITaskProgress _progress;
        private readonly CombinedRenderProgressCoordinator _progressCoordinator;
        private readonly string _cacheDir;
        private readonly string _workingDir;
        
        // Paths to intermediate files
        private string _vodPath;
        private ChatRenderer _chatRenderer;
        
        // Playlist-derived media and scaling information
        private ResolvedVideoQuality _resolvedVideoQuality;
        private ResolvedVideoDownloadPlan _videoDownloadPlan;
        private ResolvedRenderTimeline _renderTimeline;
        private VideoScalingInfo _scalingInfo;
        private ResolvedCombinedEncoder _encoder;
        
        // Cleanup flag
        private bool _shouldCleanup = true;

        public CombinedRenderer(CombinedRenderOptions options, ITaskProgress progress)
        {
            _renderOptions = options ?? throw new ArgumentNullException(nameof(options));
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _progressCoordinator = new CombinedRenderProgressCoordinator(_progress);
            
            // Validate options
            var validationError = _renderOptions.Validate();
            if (validationError != null)
                throw new ArgumentException(validationError, nameof(options));
            
            _cacheDir = CacheDirectoryService.GetCacheDirectory(_renderOptions.TempFolder);
            _workingDir = Path.Combine(_cacheDir, $"CombinedRender_{_renderOptions.Id}_{DateTimeOffset.UtcNow.Ticks}");
            
            _progress.LogVerbose($"Combined render working directory: {_workingDir}");
        }

        public async Task RenderAsync(CancellationToken cancellationToken)
        {
            var outputFileInfo = TwitchHelper.ClaimFile(_renderOptions.OutputFile, _renderOptions.FileCollisionCallback, _progress);
            _renderOptions.OutputFile = outputFileInfo.FullName;

            // Open the destination file so that it exists in the filesystem
            await using var outputFs = outputFileInfo.Open(FileMode.Create, FileAccess.Write, FileShare.Read);

            try
            {
                await RenderAsyncImpl(outputFileInfo, outputFs, cancellationToken);
            }
            catch
            {
                await Task.Delay(100, CancellationToken.None);
                TwitchHelper.CleanUpClaimedFile(outputFileInfo, outputFs, _progress);
                throw;
            }
        }

        private async Task RenderAsyncImpl(FileInfo outputFileInfo, FileStream outputFs, CancellationToken cancellationToken)
        {
            await RenderEnvironmentTelemetry.LogAsync(
                _progress,
                _renderOptions.FfmpegPath,
                cancellationToken);
            using var overallMetrics = new RenderPhaseTelemetry("combined_total", _progress, _workingDir, outputFileInfo.FullName);
            try
            {
                _progress.LogInfo("[CombinedRenderer] Starting RenderAsyncImpl");
                
                // Create working directory
                TwitchHelper.CreateDirectory(_workingDir);
                _progress.LogInfo($"[CombinedRenderer] Working directory created: {_workingDir}");
                
                // Phase 1: Fetch metadata and resolve the shared plan (5%).
                _progress.LogInfo("[CombinedRenderer] Phase 1: Fetching VOD metadata");
                _progressCoordinator.BeginPhase(
                    CombinedRenderProgressPhase.Metadata,
                    "Preparing render");
                GqlVideoResponse vodInfo;
                using (new RenderPhaseTelemetry("metadata_and_plan", _progress, _workingDir))
                {
                    vodInfo = await FetchVodInfo(cancellationToken);
                    _progressCoordinator.ReportComponent(
                        CombinedRenderProgressComponent.Metadata,
                        35);
                    _renderTimeline = CombinedRenderPlanner.ResolveTimeline(
                        _renderOptions,
                        TimeSpan.FromSeconds(vodInfo.data.video.lengthSeconds));
                    _progress.LogInfo(
                        $"Resolved output timeline: {_renderTimeline.Start:c} to {_renderTimeline.End:c} " +
                        $"({_renderTimeline.Duration:c})");

                    // Calculate scaling using the actual selected playlist quality.
                    _progress.LogInfo("[CombinedRenderer] Calculating video scaling");
                    _scalingInfo = CombinedRenderPlanner.CalculateScaling(
                        _resolvedVideoQuality.Resolution,
                        _renderOptions.OutputWidth,
                        _renderOptions.OutputHeight,
                        _renderOptions.ChatWidth,
                        _renderOptions.ChatHeight);
                    _progress.LogInfo(_scalingInfo.ToString());
                    _progress.LogInfo("[CombinedRenderer] Scaling info calculated");
                    _progressCoordinator.ReportComponent(
                        CombinedRenderProgressComponent.Metadata,
                        65);

                    using var encoderMetrics = new RenderPhaseTelemetry("encoder_probe", _progress, _workingDir);
                    _encoder = await CombinedEncoderResolver.ResolveAsync(
                        _renderOptions.FfmpegPath,
                        _renderOptions.Encoder,
                        _renderOptions.RenderProfile,
                        _workingDir,
                        _progress,
                        cancellationToken);
                    _progressCoordinator.ReportComponent(
                        CombinedRenderProgressComponent.Metadata,
                        95);
                }
                _progressCoordinator.CompleteComponent(CombinedRenderProgressComponent.Metadata);
                _progress.LogInfo("[CombinedRenderer] VOD info fetched successfully");
                
                // Phase 2: Acquire the VOD and prepare in-memory chat assets concurrently.
                _progress.LogInfo("[CombinedRenderer] Phase 2: Starting concurrent media acquisition");
                _progressCoordinator.BeginPhase(
                    CombinedRenderProgressPhase.Acquisition,
                    "Acquiring VOD and chat");
                using (new RenderPhaseTelemetry("parallel_acquisition", _progress, _workingDir))
                {
                    await AcquireMediaAsync(cancellationToken);
                }
                _progress.LogInfo("[CombinedRenderer] Concurrent media acquisition completed");
                
                // Phase 3: Composite final video (75-100%).
                _progress.LogInfo("[CombinedRenderer] Phase 3: Starting composite");
                _progressCoordinator.BeginPhase(
                    CombinedRenderProgressPhase.Composition,
                    "Compositing final video");
                
                // Close the output file before FFmpeg writes to it
                _progress.LogInfo("[CombinedRenderer] Closing output file stream");
                outputFs.Close();
                
                _progress.LogInfo("[CombinedRenderer] Starting CompositeFinalVideo");
                using (new RenderPhaseTelemetry("final_composite", _progress, _workingDir, outputFileInfo.FullName))
                {
                    await CompositeFinalVideo(outputFileInfo, cancellationToken);
                }
                _progress.LogInfo("[CombinedRenderer] CompositeFinalVideo completed");

                _progressCoordinator.CompleteComponent(CombinedRenderProgressComponent.Composition);

                try
                {
                    _progress.SetStatus("Complete");
                }
                catch (FormatException ex)
                {
                    _progress.LogError($"[CombinedRenderer] FormatException in final SetStatus: {ex.Message}");
                    _progress.LogError($"[CombinedRenderer] Stack Trace: {ex.StackTrace}");
                    throw;
                }
            }
            catch (FormatException ex)
            {
                _progress.LogError($"[CombinedRenderer] CAUGHT FormatException at top level: {ex.Message}");
                _progress.LogError($"[CombinedRenderer] Full Exception: {ex}");
                _progress.LogError($"[CombinedRenderer] Stack Trace: {ex.StackTrace}");
                throw;
            }
            catch (Exception ex)
            {
                _progress.LogError($"[CombinedRenderer] CAUGHT Exception at top level: {ex.GetType().Name}");
                _progress.LogError($"[CombinedRenderer] Message: {ex.Message}");
                _progress.LogError($"[CombinedRenderer] Full Exception: {ex}");
                throw;
            }
            finally
            {
                _chatRenderer?.Dispose();
                if (_shouldCleanup)
                {
                    CleanupTempFiles();
                }
            }
        }

        private async Task<GqlVideoResponse> FetchVodInfo(CancellationToken cancellationToken)
        {
            _progress.LogInfo($"Fetching VOD info for ID: {_renderOptions.Id}");
            var vodInfo = await TwitchHelper.GetVideoInfo(_renderOptions.Id);
            
            if (vodInfo?.data?.video == null)
            {
                throw new NullReferenceException("Invalid VOD, deleted/expired VOD possibly?");
            }
            
            _progress.LogInfo($"VOD: {vodInfo.data.video.title} by {vodInfo.data.video.owner?.displayName}");
            _progress.LogInfo($"Duration: {TimeSpan.FromSeconds(vodInfo.data.video.lengthSeconds)}");
            
            // Fetch playlist to get frame rate information
            _progress.LogInfo("Fetching video playlist to determine frame rate");
            var accessToken = await TwitchHelper.GetVideoToken(_renderOptions.Id, _renderOptions.Oauth);
            
            if (accessToken.data.videoPlaybackAccessToken is null)
            {
                throw new NullReferenceException("Unable to fetch video access token");
            }
            
            var playlistString = await TwitchHelper.GetVideoPlaylist(
                _renderOptions.Id,
                accessToken.data.videoPlaybackAccessToken.value,
                accessToken.data.videoPlaybackAccessToken.signature);
            
            if (playlistString.Contains("vod_manifest_restricted") || playlistString.Contains("unauthorized_entitlements"))
            {
                throw new NullReferenceException("Insufficient access to VOD, OAuth may be required.");
            }
            
            var videoPlaylist = M3U8.Parse(playlistString);
            videoPlaylist.SortStreamsByQuality();
            
            var qualities = VideoQualities.FromM3U8(videoPlaylist);
            _resolvedVideoQuality = CombinedRenderPlanner.ResolveVideoQuality(qualities, _renderOptions.Quality);
            _renderOptions.VodFramerate = _resolvedVideoQuality.FrameRate;

            var videoChapterResponse = await TwitchHelper.GetOrGenerateVideoChapters(
                _renderOptions.Id,
                vodInfo.data.video);
            var allQualityPaths = qualities.Qualities
                .Select(x =>
                {
                    int lastSlash = x.Path.LastIndexOf('/');
                    int secondLastSlash = x.Path.AsSpan(0, lastSlash).LastIndexOf('/');
                    return x.Path[(secondLastSlash + 1)..lastSlash];
                })
                .ToArray();
            _videoDownloadPlan = new ResolvedVideoDownloadPlan(
                vodInfo,
                videoChapterResponse,
                allQualityPaths,
                _resolvedVideoQuality.Playlist);

            _progress.LogInfo(
                $"Using playlist quality '{_resolvedVideoQuality.Name}': " +
                $"{_resolvedVideoQuality.Resolution.Width}x{_resolvedVideoQuality.Resolution.Height} at " +
                $"{_resolvedVideoQuality.FrameRate.FramesPerSecond:F3} FPS " +
                $"({_resolvedVideoQuality.FrameRate.FfmpegExpression})");
            
            return vodInfo;
        }

        private async Task DownloadVod(CancellationToken cancellationToken)
        {
            _vodPath = Path.Combine(_workingDir, "vod", "vod.mp4");
            TwitchHelper.CreateDirectory(Path.GetDirectoryName(_vodPath));
            
            var vodOptions = new VideoDownloadOptions
            {
                Id = _renderOptions.Id,
                Quality = _renderOptions.Quality,
                Oauth = _renderOptions.Oauth,
                Filename = _vodPath,
                DownloadThreads = _renderOptions.DownloadThreads,
                ThrottleKib = _renderOptions.ThrottleKib,
                TrimBeginning = _renderTimeline.TrimBeginning,
                TrimBeginningTime = _renderTimeline.Start,
                TrimEnding = _renderTimeline.TrimEnding,
                TrimEndingTime = _renderTimeline.End,
                TrimMode = VideoTrimMode.Exact,
                FfmpegPath = _renderOptions.FfmpegPath,
                TempFolder = _renderOptions.TempFolder,
                CacheCleanerCallback = _renderOptions.CacheCleanerCallback
            };
            
            var vodDownloader = new VideoDownloader(
                vodOptions,
                _progressCoordinator.CreateComponentProgress(
                    CombinedRenderProgressComponent.VodDownload,
                    "VOD Download",
                    new[] { 0.02, 0.83, 0.05, 0.10 }),
                _videoDownloadPlan);
            await vodDownloader.DownloadAsync(cancellationToken);
            _progressCoordinator.CompleteComponent(CombinedRenderProgressComponent.VodDownload);
            
            _progress.LogInfo($"VOD downloaded to: {_vodPath}");
        }

        private async Task AcquireMediaAsync(CancellationToken cancellationToken)
        {
            await CombinedRenderAcquisitionCoordinator.RunAsync(
                DownloadVodWithTelemetry,
                DownloadAndPrepareChatWithTelemetry,
                cancellationToken);
        }

        private async Task DownloadVodWithTelemetry(CancellationToken cancellationToken)
        {
            using var metrics = new RenderPhaseTelemetry("vod_download", _progress, _workingDir);
            await DownloadVod(cancellationToken);
        }

        private async Task DownloadAndPrepareChatWithTelemetry(CancellationToken cancellationToken)
        {
            ChatRoot chatRoot;
            using (new RenderPhaseTelemetry("chat_download", _progress, _workingDir))
            {
                chatRoot = await DownloadChat(cancellationToken);
            }
            _progressCoordinator.CompleteComponent(CombinedRenderProgressComponent.ChatDownload);

            _progressCoordinator.ReportComponent(CombinedRenderProgressComponent.ChatPreparation, 1);
            await WriteOptionalChatOutputAsync(chatRoot, cancellationToken);

            using var metrics = new RenderPhaseTelemetry("chat_prepare", _progress, _workingDir);
            _chatRenderer = CreateChatRenderer(chatRoot);
            await _chatRenderer.PrepareAsync(cancellationToken);
            _progressCoordinator.CompleteComponent(CombinedRenderProgressComponent.ChatPreparation);
        }

        private async Task<ChatRoot> DownloadChat(CancellationToken cancellationToken)
        {
            bool writeChatJson = !string.IsNullOrWhiteSpace(_renderOptions.ChatOutputFile);
            var chatOptions = new ChatDownloadOptions
            {
                Id = _renderOptions.Id.ToString(),
                DownloadFormat = ChatFormat.Json,
                TrimBeginning = _renderTimeline.TrimBeginning,
                TrimBeginningTime = _renderTimeline.Start.TotalSeconds,
                TrimEnding = _renderTimeline.TrimEnding,
                TrimEndingTime = _renderTimeline.End.TotalSeconds,
                EmbedData = writeChatJson && _renderOptions.EmbedChatData,
                BackfillUserInfo = writeChatJson,
                BttvEmotes = _renderOptions.BttvEmotes,
                FfzEmotes = _renderOptions.FfzEmotes,
                StvEmotes = _renderOptions.StvEmotes,
                DownloadThreads = _renderOptions.ChatDownloadThreads,
                TempFolder = _renderOptions.TempFolder
            };
            
            var chatDownloader = new ChatDownloader(
                chatOptions,
                _progressCoordinator.CreateComponentProgress(
                    CombinedRenderProgressComponent.ChatDownload,
                    "Chat Download",
                    new[] { 1d }),
                _videoDownloadPlan);
            ChatRoot chatRoot = await chatDownloader.DownloadChatRootAsync(cancellationToken);
            _progress.LogInfo($"Chat downloaded in memory: {chatRoot.comments.Count} comments");
            return chatRoot;
        }

        private async Task WriteOptionalChatOutputAsync(ChatRoot chatRoot, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_renderOptions.ChatOutputFile))
                return;

            string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(_renderOptions.ChatOutputFile));
            TwitchHelper.CreateDirectory(outputDirectory);
            FileInfo outputFile = TwitchHelper.ClaimFile(
                _renderOptions.ChatOutputFile,
                _renderOptions.FileCollisionCallback,
                _progress);
            await using var outputStream = outputFile.Open(FileMode.Create, FileAccess.Write, FileShare.Read);
            try
            {
                await ChatJson.SerializeAsync(outputStream, chatRoot, cancellationToken);
                _progress.LogInfo($"Optional chat JSON written to: {outputFile.FullName}");
            }
            catch
            {
                await Task.Delay(100, CancellationToken.None);
                TwitchHelper.CleanUpClaimedFile(outputFile, outputStream, _progress);
                throw;
            }
        }

        private ChatRenderer CreateChatRenderer(ChatRoot chatRoot)
        {
            var chatRenderOptions = new ChatRenderOptions
            {
                ChatWidth = _renderOptions.ChatWidth,
                ChatHeight = _renderOptions.ChatHeight,
                BackgroundColor = _renderOptions.ChatBackgroundColor,
                AlternateBackgroundColor = _renderOptions.ChatAlternateBackgroundColor,
                MessageColor = _renderOptions.MessageColor,
                Font = _renderOptions.Font,
                FontSize = _renderOptions.FontSize,
                Timestamp = _renderOptions.ShowTimestamps,
                ChatBadges = _renderOptions.ShowBadges,
                RenderUserAvatars = _renderOptions.ShowUserAvatars,
                AlternateMessageBackgrounds = _renderOptions.AlternateMessageBackgrounds,
                Outline = _renderOptions.Outline,
                OutlineSize = _renderOptions.OutlineSize,
                Framerate = _renderOptions.Framerate,
                UpdateRate = _renderOptions.UpdateRate,
                ReduceFramerateWhenStatic = true,
                SubMessages = _renderOptions.SubMessages,
                DisperseCommentOffsets = _renderOptions.DisperseCommentOffsets,
                FfmpegPath = _renderOptions.FfmpegPath,
                TempFolder = _renderOptions.TempFolder,
                BttvEmotes = _renderOptions.BttvEmotes,
                FfzEmotes = _renderOptions.FfzEmotes,
                StvEmotes = _renderOptions.StvEmotes,
                SkipDriveWaiting = true,
                MessageFontStyle = SKFontStyle.Normal,
                UsernameFontStyle = SKFontStyle.Bold
            };

            var chatRenderer = new ChatRenderer(
                chatRenderOptions,
                new SubTaskProgress(_progress, "Rendering Chat"));
            chatRenderer.SetChatRoot(chatRoot);
            return chatRenderer;
        }

        private async Task CompositeFinalVideo(FileInfo outputFileInfo, CancellationToken cancellationToken)
        {
            try
            {
                _progress.LogInfo("[CombinedRenderer.CompositeFinalVideo] Building filter complex");
                var filterComplex = BuildFilterComplex();
                _progress.LogInfo($"[CombinedRenderer.CompositeFinalVideo] Filter complex: {filterComplex}");
                
                _progress.LogInfo("[CombinedRenderer.CompositeFinalVideo] Building FFmpeg arguments");
                var ffmpegArgs = BuildCompositeArgs(filterComplex, outputFileInfo.FullName);
                _progress.LogInfo($"[CombinedRenderer.CompositeFinalVideo] FFmpeg args count: {ffmpegArgs.Count}");
                
                _progress.LogInfo($"Compositing with FFmpeg filter: {filterComplex}");
                
                _progress.LogInfo("[CombinedRenderer.CompositeFinalVideo] Starting ExecuteFfmpeg");
                await ExecuteFfmpegWithChatInput(ffmpegArgs, outputFileInfo.FullName, cancellationToken);
                _progress.LogInfo("[CombinedRenderer.CompositeFinalVideo] ExecuteFfmpeg completed");
                
                _progress.LogInfo($"Final video created: {outputFileInfo.FullName}");
            }
            catch (FormatException ex)
            {
                _progress.LogError($"[CombinedRenderer.CompositeFinalVideo] FormatException: {ex.Message}");
                _progress.LogError($"[CombinedRenderer.CompositeFinalVideo] Stack Trace: {ex.StackTrace}");
                throw;
            }
            catch (Exception ex)
            {
                _progress.LogError($"[CombinedRenderer.CompositeFinalVideo] Exception: {ex.GetType().Name} - {ex.Message}");
                _progress.LogError($"[CombinedRenderer.CompositeFinalVideo] Stack Trace: {ex.StackTrace}");
                throw;
            }
        }

        private string BuildFilterComplex()
        {
            return CombinedRenderPlanner.BuildFilterComplex(_scalingInfo, _renderOptions.VodFramerate);
        }

        private List<string> BuildCompositeArgs(string filterComplex, string outputPath)
        {
            string pixelFormat = SKImageInfo.PlatformColorType == SKColorType.Bgra8888
                ? "bgra"
                : "rgba";
            return CombinedRenderPlanner.BuildSinglePassCompositeArguments(
                _vodPath,
                _renderOptions.ChatWidth,
                _renderOptions.ChatHeight,
                _chatRenderer.EffectiveFramerate,
                pixelFormat,
                filterComplex,
                outputPath,
                _encoder,
                _renderOptions.FfmpegThreads);
        }

        private async Task ExecuteFfmpegWithChatInput(
            List<string> args,
            string outputPath,
            CancellationToken cancellationToken)
        {
            try
            {
                var logPath = Path.Combine(_workingDir, "ffmpeg_composite.log");
                var compositionProgress = new SubTaskProgress(_progress, "Final Composition");
                await FfmpegRunner.RunWithInputAsync(
                    _renderOptions.FfmpegPath,
                    args,
                    _workingDir,
                    logPath,
                    outputPath,
                    compositionProgress,
                    async (stream, token) =>
                    {
                        using var metrics = new RenderPhaseTelemetry("chat_render", _progress, _workingDir);
                        await _chatRenderer.RenderRawFramesAsync(stream, outputPath, token);
                    },
                    cancellationToken,
                    expectedDuration: _renderTimeline.Duration,
                    progressStart: 0,
                    progressEnd: 100,
                    detailedProgress: _progressCoordinator.ReportComposition);
            }
            catch (FfmpegExecutionException ex)
            {
                _shouldCleanup = false; // Preserve files and the FFmpeg log for diagnosis.
                _progress.LogError($"[CombinedRenderer.ExecuteFfmpeg] {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                _progress.LogError($"[CombinedRenderer.ExecuteFfmpeg] Exception: {ex.GetType().Name} - {ex.Message}");
                _progress.LogError($"[CombinedRenderer.ExecuteFfmpeg] Stack Trace: {ex.StackTrace}");
                throw;
            }
        }

        private void CleanupTempFiles()
        {
            try
            {
                if (Directory.Exists(_workingDir))
                {
                    _progress.LogVerbose($"Cleaning up temporary files at: {_workingDir}");
                    Directory.Delete(_workingDir, true);
                }
            }
            catch (Exception ex)
            {
                _progress.LogWarning($"Failed to clean up temporary files: {ex.Message}");
            }
        }

        public void Dispose()
        {
            // Cleanup is handled in finally block of RenderAsyncImpl
        }
    }

    /// <summary>
    /// Wraps an ITaskProgress to prevent sub-tasks from corrupting parent task's template status
    /// </summary>
    internal sealed class SubTaskProgress : ITaskProgress
    {
        private readonly ITaskProgress _parent;
        private readonly string _taskName;

        public SubTaskProgress(ITaskProgress parent, string taskName)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _taskName = taskName;
        }

        public void SetStatus(string status)
        {
            // Don't let sub-task modify parent status
            // Just log it instead
            _parent.LogInfo($"[{_taskName}] {status}");
        }

        public void SetTemplateStatus(string status, int initialPercent)
        {
            // Don't let sub-task modify parent template status
            // Just log it instead
            _parent.LogInfo($"[{_taskName}] {string.Format(status, initialPercent)}");
        }

        public void SetTemplateStatus(string status, int initialPercent, TimeSpan initialTime1, TimeSpan initialTime2)
        {
            // Don't let sub-task modify parent template status
            // Just log it instead
            _parent.LogInfo($"[{_taskName}] {string.Format(status, initialPercent, initialTime1, initialTime2)}");
        }

        public void ReportProgress(int percent)
        {
            // Don't let sub-task modify parent progress
            // Sub-task progress is internal only
        }

        public void ReportProgress(int percent, TimeSpan time1, TimeSpan time2)
        {
            // Don't let sub-task modify parent progress
            // Sub-task progress is internal only
        }

        public void LogVerbose(string logMessage) => _parent.LogVerbose(logMessage);
        public void LogVerbose(DefaultInterpolatedStringHandler logMessage) => _parent.LogVerbose(logMessage);
        public void LogInfo(string logMessage) => _parent.LogInfo(logMessage);
        public void LogInfo(DefaultInterpolatedStringHandler logMessage) => _parent.LogInfo(logMessage);
        public void LogWarning(string logMessage) => _parent.LogWarning(logMessage);
        public void LogWarning(DefaultInterpolatedStringHandler logMessage) => _parent.LogWarning(logMessage);
        public void LogError(string logMessage) => _parent.LogError(logMessage);
        public void LogError(DefaultInterpolatedStringHandler logMessage) => _parent.LogError(logMessage);
        public void LogFfmpeg(string logMessage) => _parent.LogFfmpeg(logMessage);
    }
}
