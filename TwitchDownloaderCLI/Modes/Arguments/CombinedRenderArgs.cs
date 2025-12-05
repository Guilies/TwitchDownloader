using CommandLine;
using TwitchDownloaderCLI.Models;
using TwitchDownloaderCore.Options;

namespace TwitchDownloaderCLI.Modes.Arguments
{
    [Verb("combinedrender", HelpText = "Downloads a VOD and chat, renders chat, and composites them into a single video")]
    internal sealed class CombinedRenderArgs : IFileCollisionArgs, ITwitchDownloaderArgs
    {
        // VOD Download Options
        [Option("id", Required = true, HelpText = "The ID or URL of the VOD to download.")]
        public string Id { get; set; }

        [Option('o', "output", Required = true, HelpText = "Path to output file.")]
        public string OutputFile { get; set; }

        [Option('q', "quality", HelpText = "The quality the program will attempt to download.")]
        public string Quality { get; set; }

        [Option("oauth", HelpText = "OAuth access token to download subscriber only VODs. DO NOT SHARE THIS WITH ANYONE.")]
        public string Oauth { get; set; }

        [Option('t', "threads", Default = 4, HelpText = "Number of parallel download threads for VOD. Large values may result in IP rate limiting.")]
        public int DownloadThreads { get; set; }

        [Option("bandwidth", Default = -1, HelpText = "The maximum bandwidth a thread will be allowed to use in kibibytes per second (KiB/s), or -1 for no maximum.")]
        public int ThrottleKib { get; set; }

        // Trim Options
        [Option('b', "beginning", HelpText = "Time to trim beginning. Can be milliseconds (#ms), seconds (#s), minutes (#m), hours (#h), or time (##:##:##).")]
        public TimeDuration TrimBeginningTime { get; set; }

        [Option('e', "ending", HelpText = "Time to trim ending. Can be milliseconds (#ms), seconds (#s), minutes (#m), hours (#h), or time (##:##:##).")]
        public TimeDuration TrimEndingTime { get; set; }

        // Output Settings
        [Option("aspect-ratio", Default = "16:9", HelpText = "Output aspect ratio: 16:9 or 4:3")]
        public string AspectRatio { get; set; }

        [Option("resolution", Default = "1080p", HelpText = "Output resolution: 4K, 1440p, 1080p, 720p, 480p, 360p")]
        public string Resolution { get; set; }

        [Option("chat-width", Default = 4, HelpText = "Chat width in aspect ratio units (1-15 for 16:9, 1-3 for 4:3)")]
        public int ChatWidthUnits { get; set; }

        // Chat Render Options
        [Option("background-color", Default = "#111111", HelpText = "The chat background color in the string format of '#RRGGBB' or '#AARRGGBB' in hexadecimal.")]
        public string BackgroundColor { get; set; }

        [Option("alt-background-color", Default = "#191919", HelpText = "The alternate message background color in the string format of '#RRGGBB' or '#AARRGGBB' in hexadecimal.")]
        public string AlternateBackgroundColor { get; set; }

        [Option("message-color", Default = "#ffffff", HelpText = "The message text color in the string format of '#RRGGBB' or '#AARRGGBB' in hexadecimal.")]
        public string MessageColor { get; set; }

        [Option('f', "font", Default = "Inter Embedded", HelpText = "Font to use for chat.")]
        public string Font { get; set; }

        [Option("font-size", Default = 12.0, HelpText = "Font size for chat.")]
        public double FontSize { get; set; }

        [Option("timestamp", Default = true, HelpText = "Enable timestamps to the left of messages.")]
        public bool ShowTimestamps { get; set; }

        [Option("badges", Default = true, HelpText = "Enable chat badges.")]
        public bool ShowBadges { get; set; }

        [Option("avatars", Default = false, HelpText = "Renders the avatars of users next to their username and badges.")]
        public bool ShowUserAvatars { get; set; }

        [Option("alternate-backgrounds", Default = false, HelpText = "Alternates the background color of every other chat message.")]
        public bool AlternateMessageBackgrounds { get; set; }

        [Option("outline", Default = false, HelpText = "Enable outline around chat messages.")]
        public bool Outline { get; set; }

        [Option("outline-size", Default = 4.0, HelpText = "Size of outline if outline is enabled.")]
        public double OutlineSize { get; set; }

        [Option("framerate", Default = 30, HelpText = "Framerate of the chat render.")]
        public int Framerate { get; set; }

        [Option("update-rate", Default = 0.2, HelpText = "Time in seconds to update chat render output.")]
        public double UpdateRate { get; set; }

        [Option("sub-messages", Default = true, HelpText = "Enable sub/re-sub messages in chat.")]
        public bool SubMessages { get; set; }

        [Option("dispersion", Default = true, HelpText = "Disperses comment offsets for more accurate message timing.")]
        public bool DisperseCommentOffsets { get; set; }

        // Emote Options
        [Option("bttv", Default = true, HelpText = "Enable BTTV emotes.")]
        public bool BttvEmotes { get; set; }

        [Option("ffz", Default = true, HelpText = "Enable FFZ emotes.")]
        public bool FfzEmotes { get; set; }

        [Option("stv", Default = true, HelpText = "Enable 7TV emotes.")]
        public bool StvEmotes { get; set; }

        [Option("embed-images", Default = true, HelpText = "Embed emotes, badges, and cheermotes into the chat download.")]
        public bool EmbedChatData { get; set; }

        // FFmpeg Options
        [Option("ffmpeg-path", HelpText = "Path to FFmpeg executable.")]
        public string FfmpegPath { get; set; }

        [Option("ffmpeg-threads", Default = 0, HelpText = "Number of threads for FFmpeg encoding. 0 = auto-detect all CPU cores. Recommended: 0 for most users.")]
        public int FfmpegThreads { get; set; }

        [Option("temp-path", Default = "", HelpText = "Path to temporary folder to use for cache.")]
        public string TempFolder { get; set; }

        // Interface args
        public OverwriteBehavior OverwriteBehavior { get; set; }
        public bool? ShowBanner { get; set; }
        public LogLevel LogLevel { get; set; }
    }
}
