using TwitchDownloaderCore.Options;

namespace TwitchDownloaderCore.ChatRender.Core
{
    /// <summary>
    /// Extends ChatRenderOptions with pre-calculated values derived from the base options
    /// </summary>
    public sealed class RenderOptions
    {
        private readonly ChatRenderOptions _baseOptions;

        // Expose base options
        public ChatRenderOptions Base => _baseOptions;

        // Pre-calculated values
        public int UpdateFrame { get; private set; }
        public double BlockArtPreWrapWidth { get; private set; }
        public bool BlockArtPreWrap { get; private set; }

        public RenderOptions(ChatRenderOptions baseOptions)
        {
            _baseOptions = baseOptions;
            CalculateDerivedValues();
        }

        private void CalculateDerivedValues()
        {
            UpdateFrame = (int)(_baseOptions.Framerate / _baseOptions.UpdateRate);
            BlockArtPreWrapWidth = 29.166 * _baseOptions.FontSize - _baseOptions.SidePadding * 2;
            BlockArtPreWrap = _baseOptions.ChatWidth > BlockArtPreWrapWidth;
        }
    }
}
