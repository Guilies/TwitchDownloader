using System.Text;
using SkiaSharp;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Tests.Fixtures
{
    internal static class GifChatFixture
    {
        internal const string Url = "https://media.giphy.com/media/fixture/giphy.gif";

        internal static ChatRoot Create(double start = 0.25, double end = 1.25)
        {
            var chat = CombinedRenderFixtureFactory.CreateChatRoot(start, end);
            chat.FileInfo = new ChatRootInfo { Version = ChatRootVersion.CurrentVersion };
            chat.comments.RemoveRange(1, chat.comments.Count - 1);
            var comment = chat.comments[0];
            comment.content_offset_seconds = start;
            comment.message.body = "Fixture GIF";
            var gif = new ChatGif { id = "fixture", url = Url };
            comment.message.fragments = new() { new() { text = "Fixture GIF", gif = gif } };
            gif.TryGetAsset(out var key, out _);
            chat.embeddedData.gifs.Add(new EmbedEmoteData { id = key, data = CreateGif(), imageScale = 1 });
            return chat;
        }

        internal static ChatRenderOptions Options(string directory, int width = 200, int height = 160) => new()
        {
            InputFile = "", OutputFile = Path.Combine(directory, "chat.mp4"), TempFolder = directory,
            ChatWidth = width, ChatHeight = height, BackgroundColor = SKColors.Black,
            MessageColor = SKColors.White, Font = "Inter Embedded", FontSize = 12,
            MessageFontStyle = SKFontStyle.Normal, UsernameFontStyle = SKFontStyle.Bold,
            Framerate = 20, UpdateRate = 0.1, Offline = true, EmojiVendor = EmojiVendor.None,
            ReduceFramerateWhenStatic = true, SkipDriveWaiting = true, DisperseCommentOffsets = false,
            FfmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg",
            InputArgs = "-y -framerate {fps} -f rawvideo -pix_fmt {pix_fmt} -video_size {width}x{height} -i -",
            OutputArgs = "-c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{save_path}\""
        };

        // Authored fixture: two solid frames, red for 200 ms then blue for 400 ms.
        // Clear the LZW dictionary for every pixel to keep the encoder intentionally simple.
        internal static byte[] CreateGif(ushort width = 96, ushort height = 64)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("GIF89a"));
            writer.Write(width); writer.Write(height);
            writer.Write(new byte[] { 0x80, 0, 0, 255, 0, 0, 0, 0, 255 });
            for (int frame = 0; frame < 2; frame++)
            {
                writer.Write(new byte[] { 0x21, 0xf9, 4, 4 });
                writer.Write((ushort)(frame == 0 ? 20 : 40));
                writer.Write(new byte[] { 0, 0, 0x2c });
                writer.Write((ushort)0); writer.Write((ushort)0);
                writer.Write(width); writer.Write(height);
                writer.Write(new byte[] { 0, 2 });
                var data = new List<byte>();
                int bits = 0, count = 0;
                void Code(int code)
                {
                    bits |= code << count;
                    count += 3;
                    while (count >= 8)
                    {
                        data.Add((byte)bits);
                        bits >>= 8;
                        count -= 8;
                    }
                }
                for (int pixel = 0; pixel < width * height; pixel++) { Code(4); Code(frame); }
                Code(5);
                if (count > 0) data.Add((byte)bits);
                for (int offset = 0; offset < data.Count; offset += 255)
                {
                    var block = data.Skip(offset).Take(255).ToArray();
                    writer.Write((byte)block.Length);
                    writer.Write(block);
                }
                writer.Write((byte)0);
            }
            writer.Write((byte)0x3b);
            return stream.ToArray();
        }
    }
}
