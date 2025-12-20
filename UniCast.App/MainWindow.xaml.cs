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
    /// DÜZELTME v50: Hafifletilmiş constructor, lazy initialization.
    /// </summary>
    public partial class MainWindow : Window, IDisposable
    {
        #region Fields

        private OverlayWindow? _overlay;
        private CancellationTokenSource? _cts;
        private bool _isDisposed;
        private readonly object _disposeLock = new();

        // Ingestor task'larını takip et
        private readonly List<Task> _ingestorTasks = new();

        // Chat Ingestors - Sadece aktif olanlar
        private YouTubeChatScraper? _ytIngestor;
        private TwitchChatIngestor? _twitchIngestor;
        private ExtensionBridgeIngestor? _extensionBridgeIngestor;

        // Stream Chat Overlay (yayına gömülü chat)
        private StreamChatOverlayService? _streamChatOverlayService;

        // ViewModels
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

        // DÜZELTME v50: Lazy init flag
        private bool _lazyInitComplete;

        #endregion

        #region Constructor

        public MainWindow()
        {
            InitializeComponent();

            _cts = new CancellationTokenSource();

            // DÜZELTME v50: Sadece kritik bağlantıları kur (hızlı)
            WireUpLogging();

            // DÜZELTME v50: UI başlat (hızlı - sadece tab event)
            InitializeUI();

            // Klavye kısayollarını etkinleştir
            PreviewKeyDown += MainWindow_PreviewKeyDown;

            // DÜZELTME v50: Loaded event'inde lazy init
            Loaded += MainWindow_Loaded;

            Log.Information("[MainWindow] Constructor tamamlandı (hafif)");
        }

        #endregion

        #region Initialization

        /// <summary>
        /// DÜZELTME v50: Window yüklendikten sonra arka planda init
        /// </summary>
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_lazyInitComplete) return;
            _lazyInitComplete = true;

            Log.Debug("[MainWindow] Lazy initialization başlıyor...");

            // Arka planda başlat - UI donmaz
            await Task.Run(() =>
            {
                try
                {
                    // 1. FFmpeg uyarısı (App'de kontrol edildi)
                    if (!App.FfmpegAvailable)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ToastService.Instance.ShowWarning("⚠️ FFmpeg bulunamadı! Stream özellikleri çalışmayabilir.");
                        });
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[MainWindow] FFmpeg uyarısı gösterilemedi");
                }
            });

            // 2. Overlay başlat (UI thread'de)
            InitializeOverlay();

            // 3. Chat sistemi (arka planda)
            _ = Task.Run(() =>
            {
                try
                {
                    Dispatcher.Invoke(() => InitializeChatSystem());
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[MainWindow] Chat sistemi başlatılamadı");
                }
            });

            Log.Debug("[MainWindow] Lazy initialization tamamlandı");
        }

        private void InitializeUI()
        {
            try
            {
                // Tab değişikliği event'i
                MainTabControl.SelectionChanged += OnTabSelectionChanged;

                // İlk tab'ı yükle (genellikle Control/Yayın Paneli)
                LoadTabContent(0);

                // İlk tab için aktif navigasyon göstergesini ayarla
                UpdateActiveNavButton(0);
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
                _ingestorTasks.Clear();

                // YouTube - Scraper (API key gerektirmez!)
                if (!string.IsNullOrWhiteSpace(settings.YouTubeVideoId))
                {
                    _ytIngestor = new YouTubeChatScraper(settings.YouTubeVideoId);
                    _ingestorTasks.Add(StartIngestorSafeAsync(_ytIngestor, "YouTube", ct));
                    Log.Information("[MainWindow] YouTube Chat Scraper aktif");
                }

                // Twitch
                if (!string.IsNullOrWhiteSpace(settings.TwitchChannelName))
                {
                    _twitchIngestor = new TwitchChatIngestor(settings.TwitchChannelName);
                    if (!string.IsNullOrWhiteSpace(settings.TwitchOAuthToken))
                    {
                        _twitchIngestor.OAuthToken = settings.TwitchOAuthToken;
                        _twitchIngestor.BotUsername = settings.TwitchBotUsername;
                    }
                    _ingestorTasks.Add(StartIngestorSafeAsync(_twitchIngestor, "Twitch", ct));
                }

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

                bool extensionBridgeNeeded = false;

                foreach (var target in targets.Where(t => t.Enabled))
                {
                    switch (target.Platform)
                    {
                        case StreamPlatform.YouTube:
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
                        case StreamPlatform.TikTok:
                        case StreamPlatform.Instagram:
                            extensionBridgeNeeded = true;
                            break;
                    }
                }

                // Extension Bridge başlat (Instagram, Facebook, TikTok için)
                if (extensionBridgeNeeded && _extensionBridgeIngestor == null)
                {
                    Log.Debug("[MainWindow] Extension Bridge başlatılıyor...");
                    _extensionBridgeIngestor = new ExtensionBridgeIngestor(9876);
                    _ingestorTasks.Add(StartIngestorSafeAsync(_extensionBridgeIngestor, "Extension Bridge", ct));

                    Log.Information("[MainWindow] Extension Bridge başlatıldı - Port: 9876");
                    Log.Information("[MainWindow] → Tarayıcınızda ilgili Live sayfasını açın");
                    Log.Information("[MainWindow] → Extension otomatik olarak yorumları aktaracak");
                }

                Log.Information("[MainWindow] Stream başladı - {Count} chat ingestor aktif", _ingestorTasks.Count);

                // Stream Chat Overlay başlat (yayına gömülü chat)
                StartStreamChatOverlay();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] OnStreamStarted hatası");
            }
        }

        /// <summary>
        /// Stream Chat Overlay'i başlatır (ayarlarda aktifse)
        /// </summary>
        private void StartStreamChatOverlay()
        {
            try
            {
                var settings = SettingsStore.Data;

                if (!settings.StreamChatOverlayEnabled)
                {
                    Log.Debug("[MainWindow] Stream Chat Overlay devre dışı");
                    return;
                }

                // Mevcut servisi temizle
                if (_streamChatOverlayService != null)
                {
                    _streamChatOverlayService.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
                    _streamChatOverlayService = null;
                }

                // Yeni servis oluştur
                _streamChatOverlayService = new StreamChatOverlayService(
                    settings.Width > 0 ? settings.Width : 1920,
                    settings.Height > 0 ? settings.Height : 1080,
                    "unicast_chat_overlay",
                    settings.Fps > 0 ? settings.Fps : 30);

                // Ayarları uygula
                var renderer = _streamChatOverlayService.Renderer;
                renderer.Position = settings.StreamChatOverlayPosition switch
                {
                    "TopLeft" => ChatOverlayPosition.TopLeft,
                    "TopRight" => ChatOverlayPosition.TopRight,
                    "BottomRight" => ChatOverlayPosition.BottomRight,
                    "Center" => ChatOverlayPosition.Center,
                    _ => ChatOverlayPosition.BottomLeft
                };
                renderer.MaxVisibleMessages = settings.StreamChatOverlayMaxMessages;
                renderer.MessageLifetimeSeconds = settings.StreamChatOverlayMessageLifetime;
                renderer.FontSize = settings.StreamChatOverlayFontSize;
                renderer.Opacity = settings.StreamChatOverlayOpacity;
                renderer.EnableShadow = settings.StreamChatOverlayShadow;

                // Servisi başlat
                _streamChatOverlayService.Start();

                Log.Information("[MainWindow] Stream Chat Overlay başlatıldı - Pozisyon: {Position}, Max: {Max} mesaj",
                    settings.StreamChatOverlayPosition, settings.StreamChatOverlayMaxMessages);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Stream Chat Overlay başlatma hatası");
            }
        }

        /// <summary>
        /// Stream Chat Overlay'i durdurur
        /// </summary>
        private async Task StopStreamChatOverlayAsync()
        {
            try
            {
                if (_streamChatOverlayService != null)
                {
                    await _streamChatOverlayService.DisposeAsync();
                    _streamChatOverlayService = null;
                    Log.Debug("[MainWindow] Stream Chat Overlay durduruldu");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MainWindow] Stream Chat Overlay durdurma hatası");
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
                _ = StopStreamChatOverlayAsync();
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
                _extensionBridgeIngestor?.StopAsync();

                _ytIngestor = null;
                _twitchIngestor = null;

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
                UpdateActiveNavButton(index);
            }
        }

        /// <summary>
        /// Aktif navigasyon butonunu işaretler
        /// </summary>
        private void UpdateActiveNavButton(int activeIndex)
        {
            var navButtons = new[] { BtnControl, BtnPreview, BtnTargets, BtnChat, BtnSettings, BtnLicense };

            for (int i = 0; i < navButtons.Length; i++)
            {
                if (i == activeIndex)
                {
                    navButtons[i].Style = (Style)FindResource("NavButtonActive");
                }
                else
                {
                    navButtons[i].Style = (Style)FindResource("NavButton");
                }
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

            _previewViewModel = new PreviewViewModel();
            _previewView = new PreviewView(_previewViewModel);

            if (PreviewTabContent != null)
                PreviewTabContent.Content = _previewView;
        }

        private void LoadTargetsTab()
        {
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

        #region Overlay Control Methods

        public void UpdateOverlaySize(double width, double height)
        {
            if (_overlay == null) return;

            try
            {
                _overlay.UpdateSize(width, height);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Overlay boyut güncelleme hatası");
            }
        }

        public void UpdateOverlayPosition(int x, int y)
        {
            if (_overlay == null) return;

            try
            {
                _overlay.UpdatePosition(x, y);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Overlay pozisyon güncelleme hatası");
            }
        }

        public void RefreshOverlay()
        {
            if (_overlay == null) return;

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

        public void StartBreak(int minutes)
        {
            if (_overlay == null) return;

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

        public void StopBreak()
        {
            if (_overlay == null) return;

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

        public void ToggleOverlayVisibility()
        {
            if (_overlay == null) return;

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

        public void SendMessageToOverlay(string message, string? sender = null)
        {
            if (_overlay == null) return;

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

        /// <summary>
        /// Global klavye kısayolları
        /// </summary>
        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // TextBox veya benzer input alanlarında klavye kısayollarını devre dışı bırak
            if (e.OriginalSource is System.Windows.Controls.TextBox)
                return;

            try
            {
                switch (e.Key)
                {
                    case System.Windows.Input.Key.F5:
                        // Yayın Başlat/Durdur
                        if (_controlViewModel != null)
                        {
                            if (_controlViewModel.IsRunning)
                            {
                                var result = MessageBox.Show(
                                    "Yayını durdurmak istediğinize emin misiniz?",
                                    "Yayını Durdur (F5)",
                                    MessageBoxButton.YesNo,
                                    MessageBoxImage.Question);

                                if (result == MessageBoxResult.Yes && _controlViewModel.StopCommand.CanExecute(null))
                                    _controlViewModel.StopCommand.Execute(null);
                            }
                            else
                            {
                                if (_controlViewModel.StartCommand.CanExecute(null))
                                    _controlViewModel.StartCommand.Execute(null);
                            }
                        }
                        e.Handled = true;
                        break;

                    case System.Windows.Input.Key.F6:
                        // Mola Modu
                        if (_controlViewModel != null && _controlViewModel.ToggleBreakCommand.CanExecute(null))
                        {
                            _controlViewModel.ToggleBreakCommand.Execute(null);
                            var status = _controlViewModel.IsOnBreak ? "başladı" : "bitti";
                            ToastService.Instance.ShowInfo($"☕ Mola {status}");
                        }
                        e.Handled = true;
                        break;

                    case System.Windows.Input.Key.F7:
                        // Mikrofon Mute/Unmute
                        if (_controlViewModel != null && _controlViewModel.ToggleMuteCommand.CanExecute(null))
                        {
                            _controlViewModel.ToggleMuteCommand.Execute(null);
                        }
                        e.Handled = true;
                        break;

                    case System.Windows.Input.Key.P:
                        // Ctrl+P - Önizleme Aç/Kapat
                        if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
                        {
                            if (_controlViewModel != null && _controlViewModel.StartPreviewCommand.CanExecute(null))
                            {
                                _controlViewModel.StartPreviewCommand.Execute(null);
                                ToastService.Instance.ShowInfo("📷 Önizleme değiştirildi");
                            }
                            e.Handled = true;
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] Klavye kısayolu hatası: {Key}", e.Key);
            }
        }

        private void OnChatMessageReceived(object? sender, ChatMessageEventArgs e)
        {
            try
            {
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
                        PreviewKeyDown -= MainWindow_PreviewKeyDown;
                        Loaded -= MainWindow_Loaded;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[MainWindow] Tab event temizleme hatası");
                    }

                    // 4. Ingestor task'larını bekle
                    if (_ingestorTasks.Count > 0)
                    {
                        try
                        {
                            Log.Debug("[MainWindow] Ingestor task'ları bekleniyor... ({Count} adet)", _ingestorTasks.Count);
                            Task.WhenAll(_ingestorTasks).Wait(TimeSpan.FromSeconds(5));
                        }
                        catch (AggregateException ae)
                        {
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

                    // 6. ViewModels'i dispose et
                    DisposeViewModels();

                    // 7. Overlay'i kapat
                    try
                    {
                        _overlay?.Close();
                        _overlay = null;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[MainWindow] Overlay kapatma hatası");
                    }

                    // 8. Stream controller'ı durdur
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

                    // 9. CTS dispose et
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
                (_extensionBridgeIngestor, "Extension Bridge")
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

            _ytIngestor = null;
            _twitchIngestor = null;
            _extensionBridgeIngestor = null;
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