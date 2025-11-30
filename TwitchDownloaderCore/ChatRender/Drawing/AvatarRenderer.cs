using System;
using SkiaSharp;
using TwitchDownloaderCore.ChatRender.Caching;
using TwitchDownloaderCore.ChatRender.Core;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.ChatRender.Drawing
{
    /// <summary>
    /// Renders user avatars for chat messages
    /// </summary>
    public sealed class AvatarRenderer
    {
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

        private readonly ChatRenderOptions _options;
        private readonly RenderContext _context;
        private readonly ImageCache _imageCache;

        public AvatarRenderer(ChatRenderOptions options, RenderContext context, ImageCache imageCache)
        {
            _options = options;
            _context = context;
            _imageCache = imageCache;
        }

        public void DrawAvatar(Comment comment, ref RenderContext.DrawingState state)
        {
            var avatarUrl = comment.commenter.logo;

            if (string.IsNullOrWhiteSpace(avatarUrl) || !_imageCache.Avatars.TryGetValue(avatarUrl, out var avatarImage))
            {
                avatarUrl = DefaultAvatarUrls[Math.Abs(comment.commenter.display_name.GetHashCode()) % DefaultAvatarUrls.Length];
                if (!_imageCache.Avatars.TryGetValue(avatarUrl, out avatarImage))
                {
                    return;
                }
            }

            // Ensure we have a valid canvas for the current section bitmap
            if (state.CurrentCanvas == null && state.SectionImages.Count > 0)
            {
                var currentBitmap = state.SectionImages[state.SectionImages.Count - 1].bitmap;
                using var tempCanvas = new SKCanvas(currentBitmap);
                state.CurrentCanvas = tempCanvas;
            }

            var avatarY = (float)((_options.SectionHeight - avatarImage.Height) / 2.0);
            state.CurrentCanvas.DrawBitmap(avatarImage, state.DrawPosition.X, avatarY);
            state.DrawPosition.X += avatarImage.Width + _options.WordSpacing;
        }
    }
}
