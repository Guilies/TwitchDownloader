using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TwitchDownloaderCore;
using TwitchDownloaderCore.Extensions;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Services;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects.Gql;
using TwitchDownloaderWPF.Models;
using TwitchDownloaderWPF.Properties;
using TwitchDownloaderWPF.Services;
using TwitchDownloaderWPF.Utils;
using WpfAnimatedGif;
using SkiaSharp;

namespace TwitchDownloaderWPF
{
    public partial class PageCombinedRender : Page
    {
        public long currentVideoId;
        public DateTime currentVideoTime;
        public TimeSpan vodLength;
        public int viewCount;
        public string game;
        public string streamerId;
        private CancellationTokenSource _cancellationTokenSource;
        private VideoScalingInfo _scalingInfo;

        public PageCombinedRender()
        {
            InitializeComponent();
        }

        private void Page_Initialized(object sender, EventArgs e)
        {
            SetEnabled(false);
            SetEnabledTrimStart(false);
            SetEnabledTrimEnd(false);

            // Load saved settings
            TextOauth.Text = Settings.Default.OAuth;
            // Note: Thread settings are loaded from UI defaults (4 for download, 0 for FFmpeg)
            
            // Initialize aspect ratio (default 16:9)
            comboAspectRatio.SelectedIndex = 0;
            
            // Initialize resolution (default 1080p)
            comboResolution.SelectedIndex = 2;
            
            // Initialize chat width units
            numChatWidthUnits.Value = 4;
            
            // Initialize other settings
            numFontSize.Value = 24;
            checkShowTimestamps.IsChecked = true;
            checkShowBadges.IsChecked = true;
            checkShowAvatars.IsChecked = false;
            checkBttvEmotes.IsChecked = true;
            checkFfzEmotes.IsChecked = true;
            checkStvEmotes.IsChecked = true;
            
            UpdateScalingPreview();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            btnDonate.Visibility = Settings.Default.HideDonation ? Visibility.Collapsed : Visibility.Visible;
            statusImage.Visibility = Settings.Default.ReduceMotion ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SetEnabled(bool isEnabled)
        {
            comboQuality.IsEnabled = isEnabled;
            comboAspectRatio.IsEnabled = isEnabled;
            comboResolution.IsEnabled = isEnabled;
            numChatWidthUnits.IsEnabled = isEnabled;
            checkStart.IsEnabled = isEnabled;
            checkEnd.IsEnabled = isEnabled;
            SplitBtnRender.IsEnabled = isEnabled;
            MenuItemEnqueue.IsEnabled = isEnabled;
            SetEnabledTrimStart(isEnabled && checkStart.IsChecked.GetValueOrDefault());
            SetEnabledTrimEnd(isEnabled && checkEnd.IsChecked.GetValueOrDefault());
        }

        private void SetEnabledTrimStart(bool isEnabled)
        {
            numStartHour.IsEnabled = isEnabled;
            numStartMinute.IsEnabled = isEnabled;
            numStartSecond.IsEnabled = isEnabled;
        }

        private void SetEnabledTrimEnd(bool isEnabled)
        {
            numEndHour.IsEnabled = isEnabled;
            numEndMinute.IsEnabled = isEnabled;
            numEndSecond.IsEnabled = isEnabled;
        }

        private async void btnGetInfo_Click(object sender, RoutedEventArgs e)
        {
            await GetVideoInfo();
        }

        private async Task GetVideoInfo()
        {
            long videoId = ValidateUrl(textUrl.Text.Trim());
            if (videoId <= 0)
            {
                MessageBox.Show(Application.Current.MainWindow!, "Invalid VOD link or ID.\n\nPlease enter a valid Twitch VOD URL or ID.", 
                    "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            currentVideoId = videoId;
            btnGetInfo.IsEnabled = false;

            try
            {
                Task<GqlVideoResponse> taskVideoInfo = TwitchHelper.GetVideoInfo(videoId);
                Task<GqlVideoTokenResponse> taskAccessToken = TwitchHelper.GetVideoToken(videoId, TextOauth.Text);
                await Task.WhenAll(taskVideoInfo, taskAccessToken);

                if (taskAccessToken.Result.data.videoPlaybackAccessToken is null)
                {
                    throw new NullReferenceException("Invalid VOD, deleted/expired VOD possibly?");
                }

                var thumbUrl = taskVideoInfo.Result.data.video.thumbnailURLs.FirstOrDefault();
                if (!ThumbnailService.TryGetThumb(thumbUrl, out var image))
                {
                    AppendLog("Error: Unable to find thumbnail");
                    _ = ThumbnailService.TryGetThumb(ThumbnailService.THUMBNAIL_MISSING_URL, out image);
                }
                imgThumbnail.Source = image;

                comboQuality.Items.Clear();

                var playlistString = await TwitchHelper.GetVideoPlaylist(videoId, 
                    taskAccessToken.Result.data.videoPlaybackAccessToken.value, 
                    taskAccessToken.Result.data.videoPlaybackAccessToken.signature);
                    
                if (playlistString.Contains("vod_manifest_restricted") || playlistString.Contains("unauthorized_entitlements"))
                {
                    throw new NullReferenceException("Insufficient access. OAuth token may be required.");
                }

                var videoPlaylist = M3U8.Parse(playlistString);
                videoPlaylist.SortStreamsByQuality();
                var qualities = VideoQualities.FromM3U8(videoPlaylist);

                foreach (var quality in qualities)
                {
                    var item = new ComboBoxItem { Content = quality.Name, Tag = quality };
                    comboQuality.Items.Add(item);
                }

                comboQuality.SelectedIndex = 0;

                vodLength = TimeSpan.FromSeconds(taskVideoInfo.Result.data.video.lengthSeconds);
                textStreamer.Text = taskVideoInfo.Result.data.video.owner?.displayName ?? "Unknown";
                streamerId = taskVideoInfo.Result.data.video.owner?.id;
                textTitle.Text = taskVideoInfo.Result.data.video.title;
                var videoCreatedAt = taskVideoInfo.Result.data.video.createdAt;
                textCreatedAt.Text = Settings.Default.UTCVideoTime 
                    ? videoCreatedAt.ToString(CultureInfo.CurrentCulture) 
                    : videoCreatedAt.ToLocalTime().ToString(CultureInfo.CurrentCulture);
                currentVideoTime = Settings.Default.UTCVideoTime ? videoCreatedAt : videoCreatedAt.ToLocalTime();
                
                var urlTimeCodeMatch = TwitchRegex.UrlTimeCode.Match(textUrl.Text);
                if (urlTimeCodeMatch.Success)
                {
                    var time = UrlTimeCode.Parse(urlTimeCodeMatch.ValueSpan);
                    checkStart.IsChecked = true;
                    numStartHour.Value = (int)time.TotalHours;
                    numStartMinute.Value = time.Minutes;
                    numStartSecond.Value = time.Seconds;
                }
                else
                {
                    numStartHour.Value = 0;
                    numStartMinute.Value = 0;
                    numStartSecond.Value = 0;
                }

                if (vodLength > TimeSpan.Zero)
                {
                    numStartHour.Maximum = (int)vodLength.TotalHours;
                    numEndHour.Maximum = (int)vodLength.TotalHours;
                }
                else
                {
                    numStartHour.Maximum = 48;
                    numEndHour.Maximum = 48;
                }

                numEndHour.Value = (int)vodLength.TotalHours;
                numEndMinute.Value = vodLength.Minutes;
                numEndSecond.Value = vodLength.Seconds;
                labelLength.Text = vodLength.ToString("c");
                viewCount = taskVideoInfo.Result.data.video.viewCount;
                game = taskVideoInfo.Result.data.video.game?.displayName ?? "Unknown";

                UpdateScalingPreview();
                SetEnabled(true);
            }
            catch (Exception ex)
            {
                btnGetInfo.IsEnabled = true;
                AppendLog("Error: " + ex.Message);
                MessageBox.Show(Application.Current.MainWindow!, "Unable to get video information.", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                if (Settings.Default.VerboseErrors)
                {
                    MessageBox.Show(Application.Current.MainWindow!, ex.ToString(), 
                        "Verbose Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void UpdateScalingPreview()
        {
            if (!IsInitialized) return;

            var aspectRatio = GetSelectedAspectRatio();
            var resolution = GetSelectedResolution();
            int chatWidthUnits = (int)numChatWidthUnits.Value;

            // Create temporary options to calculate dimensions
            var tempOptions = new CombinedRenderOptions
            {
                OutputAspectRatio = aspectRatio,
                OutputResolution = resolution,
                ChatWidthUnits = chatWidthUnits
            };

            int outputWidth = tempOptions.OutputWidth;
            int outputHeight = tempOptions.OutputHeight;
            int chatWidth = tempOptions.ChatWidth;
            int vodWidth = tempOptions.VodWidth;
            double scalingFactor = tempOptions.VodScalingFactor;

            // Update help text for chat width units
            int maxUnits = aspectRatio == AspectRatio.SixteenByNine ? 16 : 4;
            textChatWidthUnitsHelp.Text = $"({chatWidthUnits}/{maxUnits} of width)";

            // Update scaling preview
            // For display, assume VOD will be scaled to fit vodWidth
            // Actual height depends on VOD aspect ratio, but we'll estimate
            int estimatedVodHeight = (int)(vodWidth * 9.0 / 16.0); // Assume 16:9 source
            if (estimatedVodHeight > outputHeight)
            {
                estimatedVodHeight = outputHeight;
            }

            textScalingPreview.Text = $"Output: {outputWidth}x{outputHeight} | " +
                                     $"VOD: ~{vodWidth}x{estimatedVodHeight} ({scalingFactor:P0}), " +
                                     $"Chat: {chatWidth}x{outputHeight}";
        }

        private AspectRatio GetSelectedAspectRatio()
        {
            if (comboAspectRatio.SelectedItem is ComboBoxItem item)
            {
                return item.Tag.ToString() == "SixteenByNine" ? AspectRatio.SixteenByNine : AspectRatio.FourByThree;
            }
            return AspectRatio.SixteenByNine;
        }

        private OutputResolution GetSelectedResolution()
        {
            if (comboResolution.SelectedItem is ComboBoxItem item)
            {
                return item.Tag.ToString() switch
                {
                    "UHD_4K" => OutputResolution.UHD_4K,
                    "QHD_1440p" => OutputResolution.QHD_1440p,
                    "FHD_1080p" => OutputResolution.FHD_1080p,
                    "HD_720p" => OutputResolution.HD_720p,
                    "SD_480p" => OutputResolution.SD_480p,
                    "SD_360p" => OutputResolution.SD_360p,
                    _ => OutputResolution.FHD_1080p
                };
            }
            return OutputResolution.FHD_1080p;
        }

        private CombinedRenderOptions GetOptions(string filename)
        {
            var options = new CombinedRenderOptions
            {
                // VOD Download
                Id = currentVideoId,
                Quality = ((ComboBoxItem)comboQuality.SelectedItem)?.Tag?.ToString(),
                Oauth = TextOauth.Text,
                DownloadThreads = 4, // Default for now
                ThrottleKib = Settings.Default.DownloadThrottleEnabled 
                    ? Settings.Default.MaximumBandwidthKib 
                    : -1,

                // Trim Options
                TrimBeginning = checkStart.IsChecked.GetValueOrDefault(),
                TrimBeginningTime = new TimeSpan((int)numStartHour.Value, (int)numStartMinute.Value, (int)numStartSecond.Value),
                TrimEnding = checkEnd.IsChecked.GetValueOrDefault(),
                TrimEndingTime = new TimeSpan((int)numEndHour.Value, (int)numEndMinute.Value, (int)numEndSecond.Value),

                // Output Settings
                OutputAspectRatio = GetSelectedAspectRatio(),
                OutputResolution = GetSelectedResolution(),
                ChatWidthUnits = (int)numChatWidthUnits.Value,

                // Chat Settings
                FontSize = numFontSize.Value,
                ShowTimestamps = checkShowTimestamps.IsChecked.GetValueOrDefault(),
                ShowBadges = checkShowBadges.IsChecked.GetValueOrDefault(),
                ShowUserAvatars = checkShowAvatars.IsChecked.GetValueOrDefault(),
                BttvEmotes = checkBttvEmotes.IsChecked.GetValueOrDefault(),
                FfzEmotes = checkFfzEmotes.IsChecked.GetValueOrDefault(),
                StvEmotes = checkStvEmotes.IsChecked.GetValueOrDefault(),

                // File Settings
                OutputFile = filename,
                FfmpegPath = "ffmpeg",
                FfmpegThreads = 0, // Default to auto
                TempFolder = Settings.Default.TempPath
            };

            return options;
        }

        private static long ValidateUrl(string text)
        {
            var vodIdMatch = IdParse.MatchVideoId(text);
            if (vodIdMatch is { Success: true } && long.TryParse(vodIdMatch.ValueSpan, out var vodId))
            {
                return vodId;
            }
            return -1;
        }

        private void UpdateActionButtons(bool isRendering)
        {
            if (isRendering)
            {
                SplitBtnRender.Visibility = Visibility.Collapsed;
                BtnCancel.Visibility = Visibility.Visible;
                return;
            }
            SplitBtnRender.Visibility = Visibility.Visible;
            BtnCancel.Visibility = Visibility.Collapsed;
        }

        private void SetPercent(int percent)
        {
            Dispatcher.BeginInvoke(() => statusProgressBar.Value = percent);
        }

        private void SetStatus(string message)
        {
            Dispatcher.BeginInvoke(() => statusMessage.Text = message);
        }

        private void AppendLog(string message)
        {
            BtnClearLog.Dispatcher.BeginInvoke(() => BtnClearLog.IsEnabled = true);
            textLog.Dispatcher.BeginInvoke(() => textLog.AppendText(message + Environment.NewLine));
        }

        private void SetImage(string imageUri, bool isGif)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(imageUri, UriKind.Relative);
            image.EndInit();
            if (isGif)
            {
                ImageBehavior.SetAnimatedSource(statusImage, image);
            }
            else
            {
                ImageBehavior.SetAnimatedSource(statusImage, null);
                statusImage.Source = image;
            }
        }

        private async void SplitBtnRender_Click(object sender, RoutedEventArgs e)
        {
            if (((HandyControl.Controls.SplitButton)sender).IsDropDownOpen)
            {
                return;
            }

            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                Filter = "MP4 Files | *.mp4",
                FileName = FilenameService.GetFilename(
                    Settings.Default.TemplateVod, 
                    textTitle.Text, 
                    currentVideoId.ToString(), 
                    currentVideoTime, 
                    textStreamer.Text, 
                    streamerId,
                    checkStart.IsChecked == true ? new TimeSpan((int)numStartHour.Value, (int)numStartMinute.Value, (int)numStartSecond.Value) : TimeSpan.Zero,
                    checkEnd.IsChecked == true ? new TimeSpan((int)numEndHour.Value, (int)numEndMinute.Value, (int)numEndSecond.Value) : vodLength,
                    vodLength, 
                    viewCount, 
                    game) + "_with_chat.mp4"
            };

            if (saveFileDialog.ShowDialog() == false)
            {
                return;
            }

            SetEnabled(false);
            btnGetInfo.IsEnabled = false;

            CombinedRenderOptions options = GetOptions(saveFileDialog.FileName);

            var renderProgress = new WpfTaskProgress((LogLevel)Settings.Default.LogLevels, SetPercent, SetStatus, AppendLog);
            using var renderer = new CombinedRenderer(options, renderProgress);
            _cancellationTokenSource = new CancellationTokenSource();

            SetImage("Images/ppOverheat.gif", true);
            statusMessage.Text = "Rendering...";
            UpdateActionButtons(true);

            try
            {
                await renderer.RenderAsync(_cancellationTokenSource.Token);
                renderProgress.SetStatus("Complete");
                SetImage("Images/ppHop.gif", true);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException && _cancellationTokenSource.IsCancellationRequested)
            {
                renderProgress.SetStatus("Canceled");
                SetImage("Images/ppHop.gif", true);
            }
            catch (Exception ex)
            {
                renderProgress.SetStatus("Error");
                SetImage("Images/peepoSad.png", false);
                AppendLog("Error: " + ex.Message);
                if (Settings.Default.VerboseErrors)
                {
                    MessageBox.Show(Application.Current.MainWindow!, ex.ToString(), 
                        "Verbose Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            btnGetInfo.IsEnabled = true;
            SetEnabled(true);
            renderProgress.ReportProgress(0);
            _cancellationTokenSource.Dispose();
            UpdateActionButtons(false);

            GC.Collect();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            statusMessage.Text = "Canceling...";
            SetImage("Images/ppStretch.gif", true);
            try
            {
                _cancellationTokenSource?.Cancel();
            }
            catch (ObjectDisposedException) { }
        }

        private void MenuItemEnqueue_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(Application.Current.MainWindow!, 
                "Queue support for Combined Render is not yet implemented.", 
                "Coming Soon", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            BtnClearLog.IsEnabled = false;
            textLog.Dispatcher.BeginInvoke(() => textLog.Document.Blocks.Clear());
        }

        private void btnDonate_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("https://www.buymeacoffee.com/lay295") { UseShellExecute = true });
        }

        private void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settings = new WindowSettings
            {
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            settings.ShowDialog();
            btnDonate.Visibility = Settings.Default.HideDonation ? Visibility.Collapsed : Visibility.Visible;
            statusImage.Visibility = Settings.Default.ReduceMotion ? Visibility.Collapsed : Visibility.Visible;
        }

        private void TextOauth_TextChanged(object sender, RoutedEventArgs e)
        {
            if (IsInitialized)
            {
                Settings.Default.OAuth = TextOauth.Text;
                Settings.Default.Save();
            }
        }

        private void numDownloadThreads_ValueChanged(object sender, HandyControl.Data.FunctionEventArgs<double> e)
        {
            // Download threads value changed - currently not persisted
        }

        private void numFfmpegThreads_ValueChanged(object sender, HandyControl.Data.FunctionEventArgs<double> e)
        {
            // Note: We don't persist this setting yet since it requires adding a new Settings property
            // For now, it will default to 0 (auto) on each application start
        }

        private void CheckStart_OnCheckStateChanged(object sender, RoutedEventArgs e)
        {
            SetEnabledTrimStart(checkStart.IsChecked.GetValueOrDefault());
        }

        private void CheckEnd_OnCheckStateChanged(object sender, RoutedEventArgs e)
        {
            SetEnabledTrimEnd(checkEnd.IsChecked.GetValueOrDefault());
        }

        private void ComboAspectRatio_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsInitialized) return;

            // Update max chat width units based on aspect ratio
            var aspectRatio = GetSelectedAspectRatio();
            numChatWidthUnits.Maximum = aspectRatio == AspectRatio.SixteenByNine ? 15 : 3;
            
            // Ensure current value is within new range
            if (numChatWidthUnits.Value > numChatWidthUnits.Maximum)
            {
                numChatWidthUnits.Value = (int)numChatWidthUnits.Maximum / 2;
            }

            UpdateScalingPreview();
        }

        private void ComboResolution_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateScalingPreview();
        }

        private void NumChatWidthUnits_OnValueChanged(object sender, HandyControl.Data.FunctionEventArgs<double> e)
        {
            UpdateScalingPreview();
        }

        private async void TextUrl_OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await GetVideoInfo();
                e.Handled = true;
            }
        }
    }
}
