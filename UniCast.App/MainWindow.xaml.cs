using Serilog;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using UniCast.App.Overlay;
using UniCast.App.Services;
using UniCast.App.ViewModels;
using UniCast.App.Views;
using UniCast.Core.Chat;
using UniCast.Core.Chat.Ingestors;
using UniCast.Core.Models;
using UniCast.Core.Services;
using UniCast.Core.Streaming;
using MessageBox = System.Windows.MessageBox;

namespace UniCast.App
{
    /// <summary>
    /// Ana pencere - Tab yönetimi, overlay kontrolü ve kaynak koordinasyonu.
    /// Proper dispose pattern uygulanmış.
    /// </summary>
    public partial class MainWindow : Window, IDisposable
    {
        #region Fields

        private OverlayWindow? _overlay;
        private CancellationTokenSource? _cts;
        private bool _isDisposed;
        private readonly object _disposeLock = new();

        // DÜZELTME v17.3: Ingestor task'larını takip et (fire-and-forget önleme)
        private readonly List<Task> _ingestorTasks = new();

        // Chat Ingestors
        private YouTubeChatScraper? _ytIngestor;  // Scraper kullanıyoruz (API key gerektirmez)
        private TwitchChatIngestor? _twitchIngestor;
        private TikTokChatIngestor? _tikTokIngestor;
        private InstagramHybridChatIngestor? _instagramIngestor;  // DEĞİŞTİ
        private FacebookChatScraper? _facebookIngestor;  // Cookie tabanlı scraper
        private FacebookChatHost? _facebookChatHost;  // WebView2 host for Facebook chat

        // ViewModels (IDisposable olanlar)
        private PreviewViewModel? _previewViewModel;
        private ControlViewModel? _controlViewModel;
        private ChatViewModel? _chatViewModel;
        private SettingsViewModel? _settingsViewModel;
        private LicenseViewModel? _licenseViewModel;

        // Tab Views
        private PreviewView? _previewView;
        private ControlView? _controlView;
        private ChatView? _chatView;
        private SettingsView? _settingsView;
        private LicenseView? _licenseView;

        #endregion

        #region Constructor

        public MainWindow()
        {
            InitializeComponent();

            _cts = new CancellationTokenSource();

            // Loglama bağlantıları kur
            WireUpLogging();

            // FFmpeg kontrolü
            EnsureFfmpegExists();

            // UI başlat
            InitializeUI();

            // Overlay başlat
            InitializeOverlay();

            // Chat sistemini başlat
            InitializeChatSystem();

            Log.Information("[MainWindow] Başlatıldı");
        }

        #endregion

        #region Initialization

        private void InitializeUI()
        {
            try
            {
                // Tab değişikliği event'i
                MainTabControl.SelectionChanged += OnTabSelectionChanged;

                // İlk tab'ı yükle (genellikle Preview)
                LoadTabContent(0);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] UI başlatma hatası");
            }
        }

        private void InitializeOverlay()
        {
            try
            {
                Log.Debug("[MainWindow] Overlay oluşturuluyor...");
                _overlay = new OverlayWindow();

                Log.Debug("[MainWindow] Overlay.Show() çağrılıyor...");
                _overlay.Show();

                // Overlay pozisyonu ayarla (varsayılan: sağ alt köşe)
                var workArea = SystemParameters.WorkArea;
                _overlay.Left = workArea.Right - _overlay.Width - 20;
                _overlay.Top = workArea.Bottom - _overlay.Height - 20;

                Log.Debug("[MainWindow] Overlay başlatıldı");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Overlay başlatma hatası: {Message}", ex.Message);
                // Overlay olmadan da uygulama çalışabilir
                _overlay = null;
            }
        }

        private void InitializeChatSystem()
        {
            try
            {
                // ChatBus event'lerini logla
                ChatBus.Instance.MessageReceived += OnChatMessageReceived;

                // Ingestor'ları başlat
                StartChatIngestors();

                Log.Debug("[MainWindow] Chat sistemi başlatıldı");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Chat sistemi başlatma hatası");
            }
        }

        private void StartChatIngestors()
        {
            var settings = SettingsStore.Data;
            var ct = _cts?.Token ?? CancellationToken.None;

            try
            {
                // DÜZELTME v17.3: Task'ları takip et (fire-and-forget yerine)
                _ingestorTasks.Clear();

                // YouTube - Scraper (API key gerektirmez!)
                if (!string.IsNullOrWhiteSpace(settings.YouTubeVideoId))
                {
                    _ytIngestor = new YouTubeChatScraper(settings.YouTubeVideoId);
                    _ingestorTasks.Add(StartIngestorSafeAsync(_ytIngestor, "YouTube", ct));
                    Log.Information("[MainWindow] YouTube Chat Scraper aktif - API key gerektirmez");
                }

                // Twitch - YENİ!
                if (!string.IsNullOrWhiteSpace(settings.TwitchChannelName))
                {
                    _twitchIngestor = new TwitchChatIngestor(settings.TwitchChannelName);
                    // OAuth opsiyonel - anonim okuma desteklenir
                    if (!string.IsNullOrWhiteSpace(settings.TwitchOAuthToken))
                    {
                        _twitchIngestor.OAuthToken = settings.TwitchOAuthToken;
                        _twitchIngestor.BotUsername = settings.TwitchBotUsername;
                    }
                    _ingestorTasks.Add(StartIngestorSafeAsync(_twitchIngestor, "Twitch", ct));
                }

                // TikTok (Mock - API henüz implement edilmedi)
                if (!string.IsNullOrWhiteSpace(settings.TikTokUsername))
                {
                    _tikTokIngestor = new TikTokChatIngestor(settings.TikTokUsername);
                    _ingestorTasks.Add(StartIngestorSafeAsync(_tikTokIngestor, "TikTok", ct));
                    Log.Warning("[MainWindow] TikTok chat sadece mock modda çalışıyor - gerçek API henüz entegre edilmedi");
                }

                // Instagram - Hibrit API (DEĞİŞTİ)
                if (!string.IsNullOrWhiteSpace(settings.InstagramUsername))
                {
                    var hasPassword = !string.IsNullOrWhiteSpace(settings.InstagramPassword);
                    var hasToken = !string.IsNullOrWhiteSpace(settings.InstagramAccessToken) ||
                                   !string.IsNullOrWhiteSpace(settings.FacebookPageAccessToken);

                    if (hasPassword || hasToken)
                    {
                        _instagramIngestor = new InstagramHybridChatIngestor(settings.InstagramUsername)
                        {
                            Username = settings.InstagramUsername,
                            Password = settings.InstagramPassword ?? "",
                            GraphApiAccessToken = !string.IsNullOrWhiteSpace(settings.InstagramAccessToken)
                                ? settings.InstagramAccessToken
                                : settings.FacebookPageAccessToken,
                            BroadcasterUsername = string.IsNullOrWhiteSpace(settings.InstagramBroadcasterUsername)
                                ? null
                                : settings.InstagramBroadcasterUsername,
                            TotalPollingInterval = TimeSpan.FromSeconds(4)
                        };
                        _ingestorTasks.Add(StartIngestorSafeAsync(_instagramIngestor, "Instagram", ct));
                        Log.Information("[MainWindow] Instagram Hibrit API aktif");
                    }
                    else
                    {
                        Log.Warning("[MainWindow] Instagram chat için şifre veya token gerekli");
                    }
                }

                // Facebook - Yeni scraper bazlı sistem OnStreamStarted'da başlatılıyor
                // Eski mock ingestor artık kullanılmıyor

                Log.Debug("[MainWindow] {Count} adet chat ingestor başlatıldı", _ingestorTasks.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Chat ingestor başlatma hatası");
            }
        }

        private async Task StartIngestorSafeAsync(IChatIngestor ingestor, string name, CancellationToken ct)
        {
            try
            {
                Log.Debug("[MainWindow] {Name} chat ingestor başlatılıyor...", name);
                await ingestor.StartAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Log.Debug("[MainWindow] {Name} chat ingestor iptal edildi", name);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] {Name} chat ingestor hatası", name);
            }
        }

        /// <summary>
        /// Stream başladığında çağrılır - aktif platformların chat'lerini başlatır
        /// </summary>
        private void OnStreamStarted(ObservableCollection<TargetItem> targets)
        {
            try
            {
                // Önce mevcut ingestor'ları durdur
                StopAllChatIngestors();

                var ct = _cts?.Token ?? CancellationToken.None;
                _ingestorTasks.Clear();

                foreach (var target in targets.Where(t => t.Enabled))
                {
                    switch (target.Platform)
                    {
                        case StreamPlatform.YouTube:
                            // YouTube Video ID'yi ayarlardan veya URL'den çıkar
                            var videoId = ExtractYouTubeVideoId(target.Url, target.StreamKey);
                            if (!string.IsNullOrWhiteSpace(videoId))
                            {
                                _ytIngestor = new YouTubeChatScraper(videoId);
                                _ingestorTasks.Add(StartIngestorSafeAsync(_ytIngestor, "YouTube", ct));
                                Log.Information("[MainWindow] YouTube Chat başlatıldı - VideoId: {VideoId}", videoId);
                            }
                            else
                            {
                                Log.Warning("[MainWindow] YouTube Video ID bulunamadı - Chat başlatılamadı");
                            }
                            break;

                        case StreamPlatform.Twitch:
                            var channelName = SettingsStore.Data.TwitchChannelName;
                            if (!string.IsNullOrWhiteSpace(channelName))
                            {
                                _twitchIngestor = new TwitchChatIngestor(channelName);
                                if (!string.IsNullOrWhiteSpace(SettingsStore.Data.TwitchOAuthToken))
                                {
                                    _twitchIngestor.OAuthToken = SettingsStore.Data.TwitchOAuthToken;
                                    _twitchIngestor.BotUsername = SettingsStore.Data.TwitchBotUsername;
                                }
                                _ingestorTasks.Add(StartIngestorSafeAsync(_twitchIngestor, "Twitch", ct));
                                Log.Information("[MainWindow] Twitch Chat başlatıldı - Kanal: {Channel}", channelName);
                            }
                            break;

                        case StreamPlatform.Facebook:
                            // Facebook için WebView2 tabanlı FacebookChatHost kullan
                            // YENİ: Okuyucu hesap yaklaşımı - cookie yerine reader credentials kullan
                            var readerEmail = SettingsStore.Data.FacebookReaderEmail;
                            var readerPassword = SettingsStore.Data.FacebookReaderPassword;
                            var liveUrl = SettingsStore.Data.FacebookLiveVideoUrl;
                            var isReaderConnected = SettingsStore.Data.FacebookReaderConnected;

                            if (!string.IsNullOrWhiteSpace(readerEmail) &&
                                !string.IsNullOrWhiteSpace(readerPassword) &&
                                !string.IsNullOrWhiteSpace(liveUrl))
                            {
                                Log.Debug("[MainWindow] Facebook chat ingestor başlatılıyor (Okuyucu Hesap)...");

                                // UI thread'de çalıştır (WebView2 gereksinimi)
                                Dispatcher.InvokeAsync(async () =>
                                {
                                    try
                                    {
                                        // Cleanup old host
                                        if (_facebookChatHost != null)
                                        {
                                            Log.Debug("[MainWindow] Eski FacebookChatHost temizleniyor...");
                                            await _facebookChatHost.StopChatAsync();

                                            var mainGrid = Content as Grid;
                                            if (mainGrid != null && mainGrid.Children.Contains(_facebookChatHost))
                                            {
                                                mainGrid.Children.Remove(_facebookChatHost);
                                            }

                                            _facebookChatHost.Dispose();
                                            _facebookChatHost = null;
                                        }

                                        // Yeni host oluştur (okuyucu hesap bilgileri ile)
                                        _facebookChatHost = new FacebookChatHost(readerEmail, readerPassword);

                                        // MainGrid'e ekle (WebView2 için gerekli)
                                        var grid = Content as Grid;
                                        if (grid != null)
                                        {
                                            grid.Children.Add(_facebookChatHost);
                                            Log.Debug("[MainWindow] FacebookChatHost MainGrid'e eklendi");
                                        }
                                        else
                                        {
                                            Log.Warning("[MainWindow] MainGrid bulunamadı - FacebookChatHost eklenemedi");
                                        }

                                        // Chat başlat - bu SetWebViewControls() çağrısını içerir
                                        _facebookIngestor = await _facebookChatHost.StartChatAsync(liveUrl);
                                        Log.Information("[MainWindow] Facebook Chat başlatıldı (Okuyucu Hesap) - VideoUrl: {Url}",
                                            liveUrl.Length > 50 ? liveUrl.Substring(0, 50) + "..." : liveUrl);
                                    }
                                    catch (Exception ex)
                                    {
                                        Log.Error(ex, "[MainWindow] Facebook chat başlatma hatası: {Message}", ex.Message);

                                        // Cleanup on error
                                        if (_facebookChatHost != null)
                                        {
                                            var mainGrid = Content as Grid;
                                            if (mainGrid != null && mainGrid.Children.Contains(_facebookChatHost))
                                            {
                                                mainGrid.Children.Remove(_facebookChatHost);
                                            }
                                            _facebookChatHost.Dispose();
                                            _facebookChatHost = null;
                                        }
                                    }
                                });
                            }
                            else if (!string.IsNullOrWhiteSpace(readerEmail))
                            {
                                Log.Warning("[MainWindow] Facebook Chat - Okuyucu hesap var ama Live Video URL eksik");
                            }
                            else
                            {
                                Log.Warning("[MainWindow] Facebook Chat - Okuyucu hesap bilgileri eksik. Ayarlar'dan Facebook okuyucu hesap bilgilerini girin.");
                            }
                            break;

                        case StreamPlatform.TikTok:
                            var tikTokUsername = SettingsStore.Data.TikTokUsername;
                            if (!string.IsNullOrWhiteSpace(tikTokUsername))
                            {
                                _tikTokIngestor = new TikTokChatIngestor(tikTokUsername);
                                _ingestorTasks.Add(StartIngestorSafeAsync(_tikTokIngestor, "TikTok", ct));
                                Log.Warning("[MainWindow] TikTok Chat - Mock modda");
                            }
                            break;

                        case StreamPlatform.Instagram:
                            // Instagram hibrit ingestor
                            var instaUsername = SettingsStore.Data.InstagramUsername;
                            if (!string.IsNullOrWhiteSpace(instaUsername))
                            {
                                var hasPassword = !string.IsNullOrWhiteSpace(SettingsStore.Data.InstagramPassword);
                                var hasToken = !string.IsNullOrWhiteSpace(SettingsStore.Data.InstagramAccessToken);
                                if (hasPassword || hasToken)
                                {
                                    _instagramIngestor = new InstagramHybridChatIngestor(instaUsername)
                                    {
                                        Username = instaUsername,
                                        Password = SettingsStore.Data.InstagramPassword ?? "",
                                        GraphApiAccessToken = SettingsStore.Data.InstagramAccessToken
                                    };
                                    _ingestorTasks.Add(StartIngestorSafeAsync(_instagramIngestor, "Instagram", ct));
                                    Log.Information("[MainWindow] Instagram Chat başlatıldı");
                                }
                            }
                            break;
                    }
                }

                Log.Information("[MainWindow] Stream başladı - {Count} chat ingestor aktif", _ingestorTasks.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] OnStreamStarted hatası");
            }
        }

        /// <summary>
        /// Stream durduğunda çağrılır - tüm chat ingestor'ları durdurur
        /// </summary>
        private void OnStreamStopped()
        {
            try
            {
                StopAllChatIngestors();
                Log.Information("[MainWindow] Stream durdu - Chat ingestor'lar kapatıldı");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] OnStreamStopped hatası");
            }
        }

        /// <summary>
        /// Tüm chat ingestor'ları durdurur
        /// </summary>
        private void StopAllChatIngestors()
        {
            try
            {
                _ytIngestor?.StopAsync();
                _twitchIngestor?.StopAsync();
                _tikTokIngestor?.StopAsync();
                _instagramIngestor?.StopAsync();
                _facebookIngestor?.StopAsync();

                // FacebookChatHost'u UI thread'de temizle
                if (_facebookChatHost != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            _facebookChatHost.StopChatAsync().Wait(TimeSpan.FromSeconds(3));

                            var mainGrid = Content as Grid;
                            if (mainGrid != null && mainGrid.Children.Contains(_facebookChatHost))
                            {
                                mainGrid.Children.Remove(_facebookChatHost);
                            }

                            _facebookChatHost.Dispose();
                            Log.Debug("[MainWindow] FacebookChatHost temizlendi");
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "[MainWindow] FacebookChatHost temizleme hatası");
                        }
                    });
                    _facebookChatHost = null;
                }

                _ytIngestor = null;
                _twitchIngestor = null;
                _tikTokIngestor = null;
                _instagramIngestor = null;
                _facebookIngestor = null;

                _ingestorTasks.Clear();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] StopAllChatIngestors hatası");
            }
        }

        /// <summary>
        /// YouTube Video ID'yi çeşitli kaynaklardan çıkarmaya çalışır
        /// </summary>
        private string? ExtractYouTubeVideoId(string? url, string? streamKey)
        {
            // Önce ayarlarda kayıtlı Video ID'ye bak
            var settingsVideoId = SettingsStore.Data.YouTubeVideoId;
            if (!string.IsNullOrWhiteSpace(settingsVideoId))
                return settingsVideoId;

            // URL veya Stream Key'den çıkarmaya çalış
            if (!string.IsNullOrWhiteSpace(url))
            {
                // youtube.com/watch?v=VIDEO_ID formatı
                var watchMatch = Regex.Match(url, @"[?&]v=([a-zA-Z0-9_-]{11})");
                if (watchMatch.Success)
                    return watchMatch.Groups[1].Value;

                // youtu.be/VIDEO_ID formatı
                var shortMatch = Regex.Match(url, @"youtu\.be/([a-zA-Z0-9_-]{11})");
                if (shortMatch.Success)
                    return shortMatch.Groups[1].Value;

                // youtube.com/live/VIDEO_ID formatı
                var liveMatch = Regex.Match(url, @"youtube\.com/live/([a-zA-Z0-9_-]{11})");
                if (liveMatch.Success)
                    return liveMatch.Groups[1].Value;
            }

            // Stream key genellikle video ID değil, ama bazen olabilir
            if (!string.IsNullOrWhiteSpace(streamKey) && Regex.IsMatch(streamKey, @"^[a-zA-Z0-9_-]{11}$"))
                return streamKey;

            return null;
        }

        private void WireUpLogging()
        {
            try
            {
                // Stream controller log'larını Serilog'a yönlendir
                StreamController.Instance.LogMessage += (level, message) =>
                {
                    switch (level.ToLowerInvariant())
                    {
                        case "error":
                            Log.Error("[StreamController] {Message}", message);
                            break;
                        case "warning":
                            Log.Warning("[StreamController] {Message}", message);
                            break;
                        case "debug":
                            Log.Debug("[StreamController] {Message}", message);
                            break;
                        default:
                            Log.Information("[StreamController] {Message}", message);
                            break;
                    }
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Log bağlantısı kurulamadı");
            }
        }

        private void EnsureFfmpegExists()
        {
            try
            {
                var ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");

                if (!File.Exists(ffmpegPath))
                {
                    // PATH'de ara
                    var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();
                    var found = pathDirs.Any(dir =>
                    {
                        try
                        {
                            return File.Exists(Path.Combine(dir, "ffmpeg.exe"));
                        }
                        catch
                        {
                            return false;
                        }
                    });

                    if (!found)
                    {
                        Log.Warning("[MainWindow] FFmpeg bulunamadı! Stream özellikleri çalışmayabilir.");

                        Dispatcher.InvokeAsync(() =>
                        {
                            MessageBox.Show(
                                "FFmpeg bulunamadı!\n\n" +
                                "Stream özellikleri çalışmayabilir.\n" +
                                "FFmpeg'i indirip uygulama klasörüne veya PATH'e ekleyin.",
                                "Uyarı",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] FFmpeg kontrolü hatası");
            }
        }

        #endregion

        #region Tab Management

        private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != MainTabControl)
                return;

            var selectedIndex = MainTabControl.SelectedIndex;
            LoadTabContent(selectedIndex);
        }

        private void BtnNav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int index))
            {
                MainTabControl.SelectedIndex = index;
                LoadTabContent(index);
            }
        }

        private void LoadTabContent(int tabIndex)
        {
            try
            {
                switch (tabIndex)
                {
                    case 0: // Control
                        LoadControlTab();
                        break;
                    case 1: // Preview
                        LoadPreviewTab();
                        break;
                    case 2: // Targets
                        LoadTargetsTab();
                        break;
                    case 3: // Chat
                        LoadChatTab();
                        break;
                    case 4: // Settings
                        LoadSettingsTab();
                        break;
                    case 5: // License
                        LoadLicenseTab();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Tab {Index} yükleme hatası", tabIndex);
            }
        }

        private void LoadControlTab()
        {
            if (_controlView != null)
                return;

            _controlViewModel = new ControlViewModel();
            _chatViewModel = new ChatViewModel();
            _controlView = new ControlView(_controlViewModel, _chatViewModel);

            // Stream eventlerine abone ol - chat'leri yönet
            _controlViewModel.StreamStarted += OnStreamStarted;
            _controlViewModel.StreamStopped += OnStreamStopped;

            // ChatBus'ı bağla
            _controlView.BindChatBus(ChatBus.Instance);

            if (ControlTabContent != null)
                ControlTabContent.Content = _controlView;
        }

        private void LoadPreviewTab()
        {
            if (_previewView != null)
                return;

            // DÜZELTME: PreviewView kendi DataContext'ini yönetiyor (PreviewViewDataContext)
            // Dışarıdan DataContext atamıyoruz, PreviewView constructor'da hallediyor
            _previewViewModel = new PreviewViewModel();
            _previewView = new PreviewView(_previewViewModel);

            if (PreviewTabContent != null)
                PreviewTabContent.Content = _previewView;
        }

        private void LoadTargetsTab()
        {
            // TargetsView için - henüz yoksa basit bir placeholder
            if (TargetsTabContent == null || TargetsTabContent.Content != null)
                return;

            try
            {
                var targetsView = new TargetsView();
                TargetsTabContent.Content = targetsView;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MainWindow] TargetsView yüklenemedi");
            }
        }

        private void LoadChatTab()
        {
            if (_chatView != null)
                return;

            _chatViewModel = new ChatViewModel();
            _chatView = new ChatView { DataContext = _chatViewModel };

            if (ChatTabContent != null)
                ChatTabContent.Content = _chatView;
        }

        private void LoadSettingsTab()
        {
            if (_settingsView != null)
                return;

            _settingsViewModel = new SettingsViewModel();
            _settingsView = new SettingsView { DataContext = _settingsViewModel };

            if (SettingsTabContent != null)
                SettingsTabContent.Content = _settingsView;
        }

        private void LoadLicenseTab()
        {
            if (_licenseView != null)
                return;

            _licenseViewModel = new LicenseViewModel();
            _licenseView = new LicenseView { DataContext = _licenseViewModel };

            if (LicenseTabContent != null)
                LicenseTabContent.Content = _licenseView;
        }

        #endregion

        #region Overlay Control Methods (PreviewView ve ControlViewModel tarafından çağrılır)

        /// <summary>
        /// Overlay boyutunu günceller.
        /// </summary>
        public void UpdateOverlaySize(double width, double height)
        {
            if (_overlay == null)
                return;

            try
            {
                _overlay.UpdateSize(width, height);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Overlay boyut güncelleme hatası");
            }
        }

        /// <summary>
        /// Overlay pozisyonunu günceller.
        /// </summary>
        public void UpdateOverlayPosition(int x, int y)
        {
            if (_overlay == null)
                return;

            try
            {
                _overlay.UpdatePosition(x, y);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Overlay pozisyon güncelleme hatası");
            }
        }

        /// <summary>
        /// Overlay ayarlarını yeniden yükler.
        /// PreviewView.RefreshOverlayButton_Click tarafından çağrılır.
        /// </summary>
        public void RefreshOverlay()
        {
            if (_overlay == null)
                return;

            try
            {
                _overlay.ReloadSettings();
                Log.Debug("[MainWindow] Overlay yenilendi");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Overlay yenileme hatası");
            }
        }

        /// <summary>
        /// Mola modunu başlatır.
        /// ControlViewModel.StartBreakCommand tarafından çağrılır.
        /// </summary>
        public void StartBreak(int minutes)
        {
            if (_overlay == null)
                return;

            try
            {
                _overlay.StartBreakMode(minutes);
                Log.Information("[MainWindow] Mola başlatıldı: {Minutes} dakika", minutes);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Mola başlatma hatası");
            }
        }

        /// <summary>
        /// Mola modunu durdurur.
        /// ControlViewModel.StopBreakCommand tarafından çağrılır.
        /// </summary>
        public void StopBreak()
        {
            if (_overlay == null)
                return;

            try
            {
                _overlay.StopBreakMode();
                Log.Information("[MainWindow] Mola durduruldu");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Mola durdurma hatası");
            }
        }

        /// <summary>
        /// Overlay görünürlüğünü değiştirir.
        /// </summary>
        public void ToggleOverlayVisibility()
        {
            if (_overlay == null)
                return;

            try
            {
                if (_overlay.IsVisible)
                    _overlay.Hide();
                else
                    _overlay.Show();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Overlay görünürlük değiştirme hatası");
            }
        }

        /// <summary>
        /// Overlay'e mesaj gönderir.
        /// </summary>
        public void SendMessageToOverlay(string message, string? sender = null)
        {
            if (_overlay == null)
                return;

            try
            {
                _overlay.ShowMessage(message, sender ?? "System");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Overlay mesaj gönderme hatası");
            }
        }

        #endregion

        #region Event Handlers

        private void OnChatMessageReceived(object? sender, ChatMessageEventArgs e)
        {
            try
            {
                // Overlay'e mesajı gönder
                _overlay?.ShowChatMessage(e.Message);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Chat mesajı işleme hatası");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            Dispose();
            base.OnClosed(e);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            lock (_disposeLock)
            {
                if (_isDisposed)
                    return;

                if (disposing)
                {
                    Log.Debug("[MainWindow] Dispose başlatılıyor...");

                    // 1. CancellationTokenSource iptal et
                    try
                    {
                        _cts?.Cancel();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[MainWindow] CTS iptal hatası");
                    }

                    // 2. Chat event'lerini temizle
                    try
                    {
                        ChatBus.Instance.MessageReceived -= OnChatMessageReceived;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[MainWindow] ChatBus event temizleme hatası");
                    }

                    // 3. Tab event'ini temizle
                    try
                    {
                        MainTabControl.SelectionChanged -= OnTabSelectionChanged;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[MainWindow] Tab event temizleme hatası");
                    }

                    // 4. DÜZELTME v17.3: Ingestor task'larını bekle
                    if (_ingestorTasks.Count > 0)
                    {
                        try
                        {
                            Log.Debug("[MainWindow] Ingestor task'ları bekleniyor... ({Count} adet)", _ingestorTasks.Count);
                            Task.WhenAll(_ingestorTasks).Wait(TimeSpan.FromSeconds(5));
                        }
                        catch (AggregateException ae)
                        {
                            // DÜZELTME v25: Task iptal edildiğinde normal - loglama eklendi
                            System.Diagnostics.Debug.WriteLine($"[MainWindow] Ingestor task'ları AggregateException: {ae.InnerExceptions.Count} inner exception");
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "[MainWindow] Ingestor task'ları bekleme hatası");
                        }
                        finally
                        {
                            _ingestorTasks.Clear();
                        }
                    }

                    // 5. Chat Ingestors'ı durdur
                    DisposeIngestors();

                    // 5. ViewModels'i dispose et
                    DisposeViewModels();

                    // 6. Overlay'i kapat
                    try
                    {
                        _overlay?.Close();
                        _overlay = null;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[MainWindow] Overlay kapatma hatası");
                    }

                    // 7. Stream controller'ı durdur
                    try
                    {
                        if (StreamController.Instance.IsRunning)
                        {
                            StreamController.Instance.Stop();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[MainWindow] StreamController durdurma hatası");
                    }

                    // 8. CTS dispose et
                    try
                    {
                        _cts?.Dispose();
                        _cts = null;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[MainWindow] CTS dispose hatası");
                    }

                    Log.Debug("[MainWindow] Dispose tamamlandı");
                }

                _isDisposed = true;
            }
        }

        private void DisposeIngestors()
        {
            var ingestors = new List<(IChatIngestor? Ingestor, string Name)>
            {
                (_ytIngestor, "YouTube"),
                (_twitchIngestor, "Twitch"),
                (_tikTokIngestor, "TikTok"),
                (_instagramIngestor, "Instagram"),
                (_facebookIngestor, "Facebook")
            };

            foreach (var (ingestor, name) in ingestors)
            {
                if (ingestor == null)
                    continue;

                try
                {
                    ingestor.StopAsync().Wait(TimeSpan.FromSeconds(2));
                    (ingestor as IDisposable)?.Dispose();
                    Log.Debug("[MainWindow] {Name} ingestor disposed", name);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[MainWindow] {Name} ingestor dispose hatası", name);
                }
            }

            // FacebookChatHost'u ayrıca temizle
            if (_facebookChatHost != null)
            {
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            var mainGrid = Content as Grid;
                            if (mainGrid != null && mainGrid.Children.Contains(_facebookChatHost))
                            {
                                mainGrid.Children.Remove(_facebookChatHost);
                            }
                            _facebookChatHost.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "[MainWindow] FacebookChatHost UI cleanup hatası");
                        }
                    });
                    Log.Debug("[MainWindow] FacebookChatHost disposed");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[MainWindow] FacebookChatHost dispose hatası");
                }
                _facebookChatHost = null;
            }

            _ytIngestor = null;
            _twitchIngestor = null;
            _tikTokIngestor = null;
            _instagramIngestor = null;
            _facebookIngestor = null;
        }

        private void DisposeViewModels()
        {
            var viewModels = new List<(IDisposable? ViewModel, string Name)>
            {
                (_previewViewModel as IDisposable, "PreviewViewModel"),
                (_controlViewModel as IDisposable, "ControlViewModel"),
                (_chatViewModel as IDisposable, "ChatViewModel"),
                (_settingsViewModel as IDisposable, "SettingsViewModel"),
                (_licenseViewModel as IDisposable, "LicenseViewModel")
            };

            foreach (var (viewModel, name) in viewModels)
            {
                if (viewModel == null)
                    continue;

                try
                {
                    viewModel.Dispose();
                    Log.Debug("[MainWindow] {Name} disposed", name);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[MainWindow] {Name} dispose hatası", name);
                }
            }

            _previewViewModel = null;
            _controlViewModel = null;
            _chatViewModel = null;
            _settingsViewModel = null;
            _licenseViewModel = null;
        }

        ~MainWindow()
        {
            Dispose(false);
        }

        #endregion
    }
}