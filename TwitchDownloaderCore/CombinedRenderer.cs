using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Extensions;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Services;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects.Gql;

namespace TwitchDownloaderCore
{
    public sealed class CombinedRenderer : IDisposable
    {
        private readonly CombinedRenderOptions _renderOptions;
        private readonly ITaskProgress _progress;
        private readonly string _cacheDir;
        private readonly string _workingDir;
        
        // Paths to intermediate files
        private string _vodPath;
        private string _chatJsonPath;
        private string _chatVideoPath;
        
        // Scaling information
        private VideoScalingInfo _scalingInfo;
        
        // Cleanup flag
        private bool _shouldCleanup = true;

        public CombinedRenderer(CombinedRenderOptions options, ITaskProgress progress)
        {
            _renderOptions = options ?? throw new ArgumentNullException(nameof(options));
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            
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
            try
            {
                _progress.LogInfo("[CombinedRenderer] Starting RenderAsyncImpl");
                
                // Create working directory
                TwitchHelper.CreateDirectory(_workingDir);
                _progress.LogInfo($"[CombinedRenderer] Working directory created: {_workingDir}");
                
                // Phase 1: Fetch VOD metadata (5%)
                _progress.LogInfo("[CombinedRenderer] Phase 1: Fetching VOD metadata");
                try
                {
                    _progress.SetTemplateStatus("Fetching VOD Information {0}% [1/5]", 0);
                }
                catch (FormatException ex)
                {
                    _progress.LogError($"[CombinedRenderer] FormatException in Phase 1 SetTemplateStatus: {ex.Message}");
                    _progress.LogError($"[CombinedRenderer] Stack Trace: {ex.StackTrace}");
                    throw;
                }
                var vodInfo = await FetchVodInfo(cancellationToken);
                try
                {
                    _progress.ReportProgress(5);
                }
                catch (FormatException ex)
                {
                    _progress.LogError($"[CombinedRenderer] FormatException in Phase 1 ReportProgress: {ex.Message}");
                    _progress.LogError($"[CombinedRenderer] Stack Trace: {ex.StackTrace}");
                    throw;
                }
                _progress.LogInfo("[CombinedRenderer] VOD info fetched successfully");
                
                // Calculate scaling based on VOD info
                _progress.LogInfo("[CombinedRenderer] Calculating video scaling");
                _scalingInfo = CalculateVideoScaling(vodInfo);
                _progress.LogInfo(_scalingInfo.ToString());
                _progress.LogInfo("[CombinedRenderer] Scaling info calculated");
                
                // Phase 2: Download VOD (25%, 5-30%)
                _progress.LogInfo("[CombinedRenderer] Phase 2: Starting VOD download");
                try
                {
                    _progress.SetTemplateStatus("Downloading VOD {0}% [2/5]", 5);
                }
                catch (FormatException ex)
                {
                    _progress.LogError($"[CombinedRenderer] FormatException in Phase 2 SetTemplateStatus: {ex.Message}");
                    _progress.LogError($"[CombinedRenderer] Stack Trace: {ex.StackTrace}");
                    throw;
                }
                await DownloadVod(vodInfo, cancellationToken);
                try
                {
                    _progress.ReportProgress(30);
                }
                catch (FormatException ex)
                {
                    _progress.LogError($"[CombinedRenderer] FormatException in Phase 2 ReportProgress: {ex.Message}");
                    _progress.LogError($"[CombinedRenderer] Stack Trace: {ex.StackTrace}");
                    throw;
                }
                _progress.LogInfo("[CombinedRenderer] VOD download completed");
                
                // Phase 3: Download Chat (20%, 30-50%)
                _progress.LogInfo("[CombinedRenderer] Phase 3: Starting chat download");
                try
                {
                    _progress.SetTemplateStatus("Downloading Chat {0}% [3/5]", 30);
                }
                catch (FormatException ex)
                {
                    _progress.LogError($"[CombinedRenderer] FormatException in Phase 3 SetTemplateStatus: {ex.Message}");
                    _progress.LogError($"[CombinedRenderer] Stack Trace: {ex.StackTrace}");
                    throw;
                }
                await DownloadChat(vodInfo, cancellationToken);
                try
                {
                    _progress.ReportProgress(50);
                }
                catch (FormatException ex)
                {
                    _progress.LogError($"[CombinedRenderer] FormatException in Phase 3 ReportProgress: {ex.Message}");
                    _progress.LogError($"[CombinedRenderer] Stack Trace: {ex.StackTrace}");
                    throw;
                }
                _progress.LogInfo("[CombinedRenderer] Chat download completed");
                
                // Phase 4: Render Chat Video (25%, 50-75%)
                _progress.LogInfo("[CombinedRenderer] Phase 4: Starting chat video render");
                try
                {
                    _progress.SetTemplateStatus("Rendering Chat Video {0}% [4/5]", 50);
                }
                catch (FormatException ex)
                {
                    _progress.LogError($"[CombinedRenderer] FormatException in Phase 4 SetTemplateStatus: {ex.Message}");
                    _progress.LogError($"[CombinedRenderer] Stack Trace: {ex.StackTrace}");
                    throw;
                }
                await RenderChatVideo(cancellationToken);
                try
                {
                    _progress.ReportProgress(75);
                }
                catch (FormatException ex)
                {
                    _progress.LogError($"[CombinedRenderer] FormatException in Phase 4 ReportProgress: {ex.Message}");
                    _progress.LogError($"[CombinedRenderer] Stack Trace: {ex.StackTrace}");
                    throw;
                }
                _progress.LogInfo("[CombinedRenderer] Phase 4: Chat video render completed");
                
                // Phase 5: Composite Final Video (25%, 75-100%)
                _progress.LogInfo("[CombinedRenderer] Phase 5: Starting composite");
                try
                {
                    _progress.SetStatus("Compositing Final Video [5/5]");
                }
                catch (FormatException ex)
                {
                    _progress.LogError($"[CombinedRenderer] FormatException in Phase 5 SetStatus: {ex.Message}");
                    _progress.LogError($"[CombinedRenderer] Stack Trace: {ex.StackTrace}");
                    throw;
                }
                
                // Close the output file before FFmpeg writes to it
                _progress.LogInfo("[CombinedRenderer] Closing output file stream");
                outputFs.Close();
                
                _progress.LogInfo("[CombinedRenderer] Starting CompositeFinalVideo");
                await CompositeFinalVideo(outputFileInfo, cancellationToken);
                _progress.LogInfo("[CombinedRenderer] CompositeFinalVideo completed");
                
                try
                {
                    _progress.ReportProgress(100);
                }
                catch (FormatException ex)
                {
                    _progress.LogError($"[CombinedRenderer] FormatException in Phase 5 ReportProgress: {ex.Message}");
                    _progress.LogError($"[CombinedRenderer] Stack Trace: {ex.StackTrace}");
                    throw;
                }
                
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
            
            // Extract frame rate from the selected quality or highest quality
            decimal vodFramerate = 30m; // Default fallback
            var qualities = VideoQualities.FromM3U8(videoPlaylist);
            
            // Find the quality that matches the user's selection
            var selectedQuality = qualities.FirstOrDefault(q => q.Name == _renderOptions.Quality);
            if (selectedQuality != null && selectedQuality.Framerate > 0)
            {
                vodFramerate = selectedQuality.Framerate;
                _progress.LogInfo($"Using frame rate from selected quality '{_renderOptions.Quality}': {vodFramerate} FPS");
            }
            else
            {
                // Fallback to source quality
                var sourceQuality = qualities.FirstOrDefault(q => q.IsSource);
                if (sourceQuality != null && sourceQuality.Framerate > 0)
                {
                    vodFramerate = sourceQuality.Framerate;
                    _progress.LogInfo($"Using frame rate from source quality: {vodFramerate} FPS");
                }
                else
                {
                    _progress.LogWarning($"Unable to determine VOD frame rate, using default: {vodFramerate} FPS");
                }
            }
            
            // Store the framerate for later use
            _renderOptions.VodFramerate = (int)Math.Round(vodFramerate);
            
            return vodInfo;
        }

        private VideoScalingInfo CalculateVideoScaling(GqlVideoResponse vodInfo)
        {
            // Get source VOD dimensions from available qualities
            // For now, we'll use a placeholder - in real implementation, parse from quality playlist
            int sourceVodWidth = _renderOptions.OutputWidth; // Placeholder
            int sourceVodHeight = _renderOptions.OutputHeight; // Placeholder
            
            // TODO: Get actual VOD dimensions from quality playlist
            _progress.LogWarning("Using placeholder VOD dimensions. Actual dimensions will be detected from playlist.");
            
            var outputHeight = _renderOptions.OutputHeight;
            var outputWidth = _renderOptions.OutputWidth;
            var chatWidth = _renderOptions.ChatWidth;
            var chatHeight = _renderOptions.ChatHeight;
            
            // Calculate remaining width for VOD
            var remainingWidth = outputWidth - chatWidth;
            
            // Scale VOD to fit remaining width while maintaining aspect ratio
            var vodAspectRatio = (double)sourceVodWidth / sourceVodHeight;
            var scaledVodWidth = remainingWidth;
            var scaledVodHeight = (int)(remainingWidth / vodAspectRatio);
            
            // If scaled VOD height exceeds output height, scale down to fit
            if (scaledVodHeight > outputHeight)
            {
                scaledVodHeight = outputHeight;
                scaledVodWidth = (int)(outputHeight * vodAspectRatio);
            }
            
            // Ensure even dimensions for H.264 encoding
            scaledVodWidth = (scaledVodWidth / 2) * 2;
            scaledVodHeight = (scaledVodHeight / 2) * 2;
            
            var scalingInfo = new VideoScalingInfo
            {
                SourceVodWidth = sourceVodWidth,
                SourceVodHeight = sourceVodHeight,
                ScaledVodWidth = scaledVodWidth,
                ScaledVodHeight = scaledVodHeight,
                ChatWidth = chatWidth,
                ChatHeight = chatHeight,
                OutputWidth = outputWidth,
                OutputHeight = outputHeight,
                VodScalingFactor = (double)scaledVodWidth / sourceVodWidth
            };
            
            scalingInfo.CalculateLetterboxPadding();
            
            return scalingInfo;
        }

        private async Task DownloadVod(GqlVideoResponse vodInfo, CancellationToken cancellationToken)
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
                TrimBeginning = _renderOptions.TrimBeginning,
                TrimBeginningTime = _renderOptions.TrimBeginningTime,
                TrimEnding = _renderOptions.TrimEnding,
                TrimEndingTime = _renderOptions.TrimEndingTime,
                TrimMode = _renderOptions.TrimMode,
                FfmpegPath = _renderOptions.FfmpegPath,
                TempFolder = _renderOptions.TempFolder,
                CacheCleanerCallback = _renderOptions.CacheCleanerCallback
            };
            
            var vodDownloader = new VideoDownloader(vodOptions, _progress);
            await vodDownloader.DownloadAsync(cancellationToken);
            
            _progress.LogInfo($"VOD downloaded to: {_vodPath}");
        }

        private async Task DownloadChat(GqlVideoResponse vodInfo, CancellationToken cancellationToken)
        {
            _chatJsonPath = Path.Combine(_workingDir, "chat", "chat.json");
            TwitchHelper.CreateDirectory(Path.GetDirectoryName(_chatJsonPath));
            
            var chatOptions = new ChatDownloadOptions
            {
                Id = _renderOptions.Id.ToString(),
                DownloadFormat = ChatFormat.Json,
                Filename = _chatJsonPath,
                Compression = ChatCompression.None,
                TrimBeginning = _renderOptions.TrimBeginning,
                TrimBeginningTime = _renderOptions.TrimBeginning ? _renderOptions.TrimBeginningTime.TotalSeconds : 0,
                TrimEnding = _renderOptions.TrimEnding,
                TrimEndingTime = _renderOptions.TrimEnding ? _renderOptions.TrimEndingTime.TotalSeconds : vodInfo.data.video.lengthSeconds,
                EmbedData = _renderOptions.EmbedChatData,
                BttvEmotes = _renderOptions.BttvEmotes,
                FfzEmotes = _renderOptions.FfzEmotes,
                StvEmotes = _renderOptions.StvEmotes,
                DownloadThreads = 1, // Chat download is fast enough with 1 thread
                TempFolder = _renderOptions.TempFolder
            };
            
            var chatDownloader = new ChatDownloader(chatOptions, _progress);
            await chatDownloader.DownloadAsync(cancellationToken);
            
            _progress.LogInfo($"Chat downloaded to: {_chatJsonPath}");
        }

        private async Task RenderChatVideo(CancellationToken cancellationToken)
        {
            _progress.LogInfo("[CombinedRenderer.RenderChatVideo] Starting chat video render");
            _chatVideoPath = Path.Combine(_workingDir, "chat", "chat.mp4");
            
            _progress.LogInfo($"[CombinedRenderer.RenderChatVideo] ChatWidth={_renderOptions.ChatWidth}, ChatHeight={_renderOptions.ChatHeight}");
            
            var chatRenderOptions = new ChatRenderOptions
            {
                InputFile = _chatJsonPath,
                OutputFile = _chatVideoPath,
                ChatWidth = _renderOptions.ChatWidth,
                ChatHeight = _renderOptions.ChatHeight, // Full output height!
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
                SubMessages = _renderOptions.SubMessages,
                DisperseCommentOffsets = _renderOptions.DisperseCommentOffsets,
                FfmpegPath = _renderOptions.FfmpegPath,
                TempFolder = _renderOptions.TempFolder,
                BttvEmotes = _renderOptions.BttvEmotes,
                FfzEmotes = _renderOptions.FfzEmotes,
                StvEmotes = _renderOptions.StvEmotes,
                // CRITICAL: Set FFmpeg arguments for chat render
                InputArgs = "-framerate {fps} -f rawvideo -analyzeduration {max_int} -probesize {max_int} -pix_fmt {pix_fmt} -video_size {width}x{height} -i -",
                OutputArgs = "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p \"{save_path}\"",
                // Set default font styles
                MessageFontStyle = SkiaSharp.SKFontStyle.Normal,
                UsernameFontStyle = SkiaSharp.SKFontStyle.Bold
            };
            
            _progress.LogInfo("[CombinedRenderer.RenderChatVideo] ChatRenderOptions created successfully");
            _progress.LogInfo($"[CombinedRenderer.RenderChatVideo] InputArgs: {chatRenderOptions.InputArgs}");
            _progress.LogInfo($"[CombinedRenderer.RenderChatVideo] OutputArgs: {chatRenderOptions.OutputArgs}");
            
            // Create a wrapper progress that prevents ChatRenderer from corrupting our template status
            var chatProgress = new SubTaskProgress(_progress, "Rendering Chat Video");
            
            _progress.LogInfo("[CombinedRenderer.RenderChatVideo] Creating ChatRenderer instance");
            using var chatRenderer = new ChatRenderer(chatRenderOptions, chatProgress);
            
            _progress.LogInfo("[CombinedRenderer.RenderChatVideo] Parsing chat JSON");
            await chatRenderer.ParseJsonAsync(cancellationToken);
            _progress.LogInfo("[CombinedRenderer.RenderChatVideo] Chat JSON parsed successfully");
            
            _progress.LogInfo("[CombinedRenderer.RenderChatVideo] Starting RenderVideoAsync");
            await chatRenderer.RenderVideoAsync(cancellationToken);
            _progress.LogInfo("[CombinedRenderer.RenderChatVideo] RenderVideoAsync completed");
            
            _progress.LogInfo($"Chat rendered to: {_chatVideoPath}");
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
                await ExecuteFfmpeg(ffmpegArgs, outputFileInfo.FullName, cancellationToken);
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
            var fps = _renderOptions.VodFramerate;
            var sb = new StringBuilder();
            
            // Normalize VOD frame rate FIRST, then scale
            sb.Append($"[0:v]fps={fps}[vod_fps];");
            sb.Append($"[vod_fps]scale={_scalingInfo.ScaledVodWidth}:{_scalingInfo.ScaledVodHeight}:flags=lanczos");
            
            if (_scalingInfo.RequiresLetterboxing)
            {
                sb.Append($",pad={_scalingInfo.ScaledVodWidth}:{_renderOptions.OutputHeight}:0:{_scalingInfo.LetterboxPaddingTop}:black");
            }
            
            sb.Append("[vod];");
            
            // Normalize chat frame rate too
            sb.Append($"[1:v]fps={fps}[chat_fps];");
            sb.Append($"[chat_fps]scale={_scalingInfo.ChatWidth}:{_scalingInfo.ChatHeight}[chat];");
            
            // Stack horizontally: VOD on left, chat on right
            sb.Append("[vod][chat]hstack=inputs=2");
            
            return sb.ToString();
        }

        private List<string> BuildCompositeArgs(string filterComplex, string outputPath)
        {
            var args = new List<string>
            {
                "-y", // Overwrite output file

                "-threads", _renderOptions.FfmpegThreads.ToString(), // Configurable CPU threads
                "-filter_threads", _renderOptions.FfmpegThreads.ToString(), // Match filter threads to encoder threads
                "-filter_complex_threads", _renderOptions.FfmpegThreads.ToString(), // Match filter_complex threads

                "-i", _vodPath,
                "-i", _chatVideoPath,

                "-filter_complex", filterComplex,

                "-r", _renderOptions.VodFramerate.ToString(), // Output frame rate
                "-vsync", "cfr",                 // Constant frame rate
                
                "-c:v", "libx264",
                "-preset", "medium",
                "-crf", "23",
                "-pix_fmt", "yuv420p",

                "-c:a", "copy", // Copy audio from VOD

                outputPath
            };

            return args;
        }

        private async Task ExecuteFfmpeg(List<string> args, string outputPath, CancellationToken cancellationToken)
        {
            try
            {
                _progress.LogInfo("[CombinedRenderer.ExecuteFfmpeg] Creating FFmpeg process");
                
                using var process = new Process
                {
                    StartInfo =
                    {
                        FileName = _renderOptions.FfmpegPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = _workingDir
                    }
                };
                
                _progress.LogInfo("[CombinedRenderer.ExecuteFfmpeg] Adding arguments to process");
                foreach (var arg in args)
                {
                    process.StartInfo.ArgumentList.Add(arg);
                }
                
                _progress.LogInfo("[CombinedRenderer.ExecuteFfmpeg] Creating log file");
                var logPath = Path.Combine(_workingDir, "ffmpeg_composite.log");
                await using var logWriter = File.CreateText(logPath);
                
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        try
                        {
                            logWriter.WriteLine(e.Data);
                            _progress.LogFfmpeg(e.Data);
                        }
                        catch (FormatException ex)
                        {
                            _progress.LogError($"[CombinedRenderer.ExecuteFfmpeg] FormatException in ErrorDataReceived: {ex.Message}");
                            _progress.LogError($"[CombinedRenderer.ExecuteFfmpeg] Problematic data: {e.Data}");
                        }
                        catch (Exception ex)
                        {
                            _progress.LogError($"[CombinedRenderer.ExecuteFfmpeg] Exception in ErrorDataReceived: {ex.GetType().Name} - {ex.Message}");
                        }
                    }
                };
                
                // Build command string safely without using string.Format
                var commandString = _renderOptions.FfmpegPath + " " + string.Join(" ", args);
                _progress.LogInfo("[CombinedRenderer.ExecuteFfmpeg] About to log FFmpeg command");
                try
                {
                    _progress.LogVerbose($"Running FFmpeg: {commandString}");
                }
                catch (FormatException ex)
                {
                    _progress.LogError($"[CombinedRenderer.ExecuteFfmpeg] FormatException when logging command: {ex.Message}");
                    _progress.LogError($"[CombinedRenderer.ExecuteFfmpeg] Command was: {commandString}");
                    _progress.LogError($"[CombinedRenderer.ExecuteFfmpeg] Stack Trace: {ex.StackTrace}");
                    throw;
                }
                
                _progress.LogInfo("[CombinedRenderer.ExecuteFfmpeg] Starting FFmpeg process");
                process.Start();
                process.BeginErrorReadLine();
                
                _progress.LogInfo("[CombinedRenderer.ExecuteFfmpeg] Waiting for FFmpeg to complete");
                await process.WaitForExitAsync(cancellationToken);
                _progress.LogInfo($"[CombinedRenderer.ExecuteFfmpeg] FFmpeg exited with code: {process.ExitCode}");
                
                if (process.ExitCode != 0)
                {
                    _shouldCleanup = false; // Preserve files for debugging
                    throw new Exception($"FFmpeg failed with exit code {process.ExitCode}. See log at: {logPath}");
                }
            }
            catch (FormatException ex)
            {
                _progress.LogError($"[CombinedRenderer.ExecuteFfmpeg] FormatException: {ex.Message}");
                _progress.LogError($"[CombinedRenderer.ExecuteFfmpeg] Stack Trace: {ex.StackTrace}");
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

