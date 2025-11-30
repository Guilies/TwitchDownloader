using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.ChatRender.Caching;
using TwitchDownloaderCore.ChatRender.Core;
using TwitchDownloaderCore.ChatRender.Drawing;
using TwitchDownloaderCore.ChatRender.Message;
using TwitchDownloaderCore.ChatRender.Processing;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Services;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;
using SkiaSharp;

namespace TwitchDownloaderCore
{
    public sealed class ChatRenderer : IDisposable
    {
        public bool Disposed { get; private set; } = false;
        public ChatRoot chatRoot { get; private set; } = new ChatRoot();

        private static readonly string[] DefaultAvatarUrls =
        {
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/75305d54-c7cc-40d1-bb9c-91fbe85943c7-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/ebe4cd89-b4f4-4cd9-adac-2f30151b4209-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/215b7342-def9-11e9-9a66-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/cdd517fe-def4-11e9-948e-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/41780b5a-def8-11e9-94d9-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/13e5fa74-defa-11e9-809c-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/de130ab0-def7-11e9-b668-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/ead5c8b2-a4c9-4724-b1dd-9f00b46cbd3d-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/ce57700a-def9-11e9-842d-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/998f01ae-def8-11e9-b95c-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/dbdc9198-def8-11e9-8681-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/294c98b5-e34d-42cd-a8f0-140b72fba9b0-profile_image-70x70.png",
        };

        private readonly ITaskProgress _progress;
        private readonly ChatRenderOptions renderOptions;
        private readonly string _cacheDir;
        
        // Refactored component instances
        private readonly RenderContext _context;
        private readonly BitmapCache _bitmapCache;
        private readonly ImageCache _imageCache;
        private readonly FontCache _fontCache;
        private readonly CommentProcessor _commentProcessor;
        private readonly ImageFetcher _imageFetcher;
        private readonly SectionRenderer _sectionRenderer;

        public ChatRenderer(ChatRenderOptions chatRenderOptions, ITaskProgress progress)
        {
            renderOptions = chatRenderOptions;
            _cacheDir = CacheDirectoryService.GetCacheDirectory(renderOptions.TempFolder);
            _progress = progress;
            
            // Initialize core infrastructure
            _context = new RenderContext(renderOptions);
            _bitmapCache = new BitmapCache();
            _imageCache = new ImageCache();
            _fontCache = new FontCache(renderOptions, progress);
            
            // Initialize processing components
            _commentProcessor = new CommentProcessor(renderOptions);
            _imageFetcher = new ImageFetcher(_cacheDir, renderOptions, progress, DefaultAvatarUrls);
            
            // Create SectionRenderer first (needed for AddImageSection callback)
            var sectionRenderer = new SectionRenderer(
                renderOptions,
                _context,
                _bitmapCache,
                _imageCache,
                _fontCache,
                null, // timestampRenderer - will be set after creation
                null, // avatarRenderer
                null, // badgeRenderer
                null, // textRenderer
                null, // messageRenderer
                null, // emoteRenderer
                new HighlightIcons(renderOptions, _cacheDir, SKColor.Parse("#7B2CF2"), _fontCache.OutlinePaint),
                progress
            );
            
            // Helper callback for adding image sections
            Action<RenderContext.DrawingState, Point> addImageSectionCallback = (state, defaultPos) =>
            {
                sectionRenderer.AddImageSection(ref state, defaultPos);
            };
            
            // Helper callback for checking and wrapping if needed
            Func<RenderContext.DrawingState, int, bool> checkAndWrapCallback = (state, elementWidth) =>
            {
                return sectionRenderer.CheckAndWrapIfNeeded(ref state, elementWidth);
            };
            
            // Helper callback for ensuring canvas exists
            Action<RenderContext.DrawingState> ensureCanvasCallback = (state) =>
            {
                sectionRenderer.EnsureCanvas(ref state);
            };
            
            // Initialize renderers (dependency chain) and wire them into the SectionRenderer
            var timestampRenderer = new TimestampRenderer(renderOptions, _context, _bitmapCache, _fontCache, addImageSectionCallback);
            var avatarRenderer = new AvatarRenderer(renderOptions, _context, _imageCache);
            var badgeRenderer = new BadgeRenderer(renderOptions, _context, _imageCache, _bitmapCache, addImageSectionCallback);
            var textRenderer = new TextRenderer(renderOptions, _context, _fontCache, _bitmapCache, addImageSectionCallback);
            var emoteRenderer = new EmoteRenderer(renderOptions, _context, _imageCache, _bitmapCache, addImageSectionCallback, checkAndWrapCallback, ensureCanvasCallback);
            var messageRenderer = new MessageRenderer(renderOptions, _context, _imageCache, _fontCache, _bitmapCache, textRenderer, emoteRenderer, addImageSectionCallback, checkAndWrapCallback, ensureCanvasCallback);
            
            // Wire the renderers into the SectionRenderer that was created earlier
            sectionRenderer.SetRenderers(timestampRenderer, avatarRenderer, badgeRenderer, textRenderer, messageRenderer, emoteRenderer);
            _sectionRenderer = sectionRenderer;
        }

        public async Task RenderVideoAsync(CancellationToken cancellationToken)
        {
            var outputFileInfo = TwitchHelper.ClaimFile(renderOptions.OutputFile, renderOptions.FileCollisionCallback, _progress);
            renderOptions.OutputFile = outputFileInfo.FullName;
            var maskFileInfo = renderOptions.GenerateMask ? TwitchHelper.ClaimFile(renderOptions.MaskFile, renderOptions.FileCollisionCallback, _progress) : null;

            // Open the destination files so that they exist in the filesystem.
            await using var outputFs = outputFileInfo.Open(FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var maskFs = maskFileInfo?.Open(FileMode.Create, FileAccess.Write, FileShare.Read);

            try
            {
                await RenderAsyncImpl(outputFileInfo, outputFs, maskFileInfo, maskFs, cancellationToken);
            }
            catch
            {
                await Task.Delay(100, CancellationToken.None);

                TwitchHelper.CleanUpClaimedFile(outputFileInfo, outputFs, _progress);
                TwitchHelper.CleanUpClaimedFile(maskFileInfo, maskFs, _progress);

                throw;
            }
        }

        private async Task RenderAsyncImpl(FileInfo outputFileInfo, FileStream outputFs, FileInfo maskFileInfo, FileStream maskFs, CancellationToken cancellationToken)
        {
            _progress.SetStatus("Fetching Images [1/2]");
            var fetchedImages = await _imageFetcher.FetchAllImagesAsync(chatRoot, cancellationToken);
            
            // Initialize image cache with fetched images
            _imageCache.Initialize(
                fetchedImages.Badges,
                fetchedImages.Emotes,
                fetchedImages.ThirdPartyEmotes,
                fetchedImages.Cheermotes,
                fetchedImages.Emojis,
                fetchedImages.Avatars
            );
            
            // Process comments (disperse, floor, remove restricted)
            _commentProcessor.ProcessComments(chatRoot.comments);
            
            // Initialize fonts typefaces and geometry context
            _fontCache.SetTypefaces(renderOptions.UsernameFontStyle, renderOptions.MessageFontStyle);
            _context.InitializeGeometry(_fontCache.MessageFont);
            
            // Calculate BlockArtPreWrap values
            renderOptions.BlockArtPreWrapWidth = 29.166 * renderOptions.FontSize - renderOptions.SidePadding * 2;
            renderOptions.BlockArtPreWrap = renderOptions.ChatWidth > renderOptions.BlockArtPreWrapWidth;

            // Clear embedded data to save memory
            chatRoot.embeddedData = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();

            (int startTick, int totalTicks) = GetVideoTicks();

            // Delete the files as it is not guaranteed that the overwrite flag is passed in the FFmpeg args.
            outputFs.Close();
            outputFileInfo.Refresh();
            if (outputFileInfo.Exists)
                outputFileInfo.Delete();

            maskFs?.Close();
            maskFileInfo?.Refresh();
            if (renderOptions.GenerateMask && maskFileInfo!.Exists)
                maskFileInfo.Delete();

            FfmpegProcess ffmpegProcess = GetFfmpegProcess(outputFileInfo);
            FfmpegProcess maskProcess = renderOptions.GenerateMask ? GetFfmpegProcess(maskFileInfo) : null;
            _progress.SetTemplateStatus(@"Rendering Video {0}% ({1:h\hm\ms\s} Elapsed | {2:h\hm\ms\s} Remaining)", 0, TimeSpan.Zero, TimeSpan.Zero);

            try
            {
                // Delegate rendering to SectionRenderer
                _sectionRenderer.SetChatRoot(chatRoot);
                await Task.Run(() => _sectionRenderer.RenderSection(startTick, startTick + totalTicks, ffmpegProcess, maskProcess, cancellationToken), cancellationToken);
            }
            catch
            {
                ffmpegProcess.Dispose();
                maskProcess?.Dispose();
                GC.Collect();
                throw;
            }
        }

        private FfmpegProcess GetFfmpegProcess(FileInfo fileInfo)
        {
            string savePath = fileInfo.FullName;

            string inputArgs = new StringBuilder(renderOptions.InputArgs)
                .Replace("{fps}", renderOptions.Framerate.ToString())
                .Replace("{height}", renderOptions.ChatHeight.ToString())
                .Replace("{width}", renderOptions.ChatWidth.ToString())
                .Replace("{save_path}", savePath)
                .Replace("{max_int}", int.MaxValue.ToString())
                .Replace("{pix_fmt}", SKImageInfo.PlatformColorType == SKColorType.Bgra8888 ? "bgra" : "rgba")
                .ToString();
            string outputArgs = new StringBuilder(renderOptions.OutputArgs)
                .Replace("{fps}", renderOptions.Framerate.ToString())
                .Replace("{height}", renderOptions.ChatHeight.ToString())
                .Replace("{width}", renderOptions.ChatWidth.ToString())
                .Replace("{save_path}", savePath)
                .Replace("{max_int}", int.MaxValue.ToString())
                .ToString();

            var process = new FfmpegProcess
            {
                StartInfo =
                {
                    FileName = renderOptions.FfmpegPath,
                    Arguments = $"{inputArgs} {outputArgs}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                SavePath = savePath
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    _progress.LogFfmpeg(e.Data);
                }
            };

            _progress.LogVerbose($"Running \"{renderOptions.FfmpegPath}\" in \"{process.StartInfo.WorkingDirectory}\" with args: {process.StartInfo.Arguments}");

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            return process;
        }

        private (int startTick, int totalTicks) GetVideoTicks()
        {
            if (renderOptions.StartOverride != -1 && renderOptions.EndOverride != -1)
            {
                int startSeconds = renderOptions.StartOverride;
                int videoStartTick = startSeconds * renderOptions.Framerate;
                int totalTicks = renderOptions.EndOverride * renderOptions.Framerate - videoStartTick;
                return (videoStartTick, totalTicks);
            }
            else
            {
                int startSeconds = (int)Math.Floor(chatRoot.video.start);
                int videoStartTick = startSeconds * renderOptions.Framerate;
                int totalTicks = (int)Math.Ceiling(chatRoot.video.end * renderOptions.Framerate) - videoStartTick;
                return (videoStartTick, totalTicks);
            }
        }

        public async Task<ChatRoot> ParseJsonAsync(CancellationToken cancellationToken = new())
        {
            chatRoot = await ChatJson.DeserializeAsync(renderOptions.InputFile, true, false, true, cancellationToken);
            return chatRoot;
        }

        #region ImplementIDisposable

        public void Dispose()
        {
            Dispose(true);
        }

        private void Dispose(bool isDisposing)
        {
            try
            {
                if (Disposed)
                {
                    return;
                }

                if (isDisposing)
                {
                    _bitmapCache?.Dispose();
                    _imageCache?.Dispose();
                    _fontCache?.Dispose();
                    _context?.Dispose();
                    // SectionRenderer doesn't implement IDisposable
                    
                    chatRoot = null;
                }
            }
            finally
            {
                Disposed = true;
            }
        }

        #endregion
    }
}
