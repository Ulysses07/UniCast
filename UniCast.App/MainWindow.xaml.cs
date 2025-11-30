using System;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using UniCast.App.Overlay;
using UniCast.App.Services;
using UniCast.App.Services.Capture;
using UniCast.App.Services.Chat;
using UniCast.App.ViewModels;
using UniCast.App.Views;
using UniCast.Core.Chat;
using Serilog;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox; // Button çakışmasını önle

namespace UniCast.App
{
    public partial class MainWindow : Window
    {
        private readonly IDeviceService _deviceService;
        private readonly IStreamController _stream;
        private readonly SettingsViewModel _settingsVm;
        private readonly TargetsViewModel _targetsVm;
        private readonly ControlViewModel _controlVm;

        private readonly ChatBus _chatBus = new();
        private readonly ChatViewModel _chatVm = new();

        private YouTubeChatIngestor? _ytIngestor;
        private TikTokChatIngestor? _tiktok;
        private InstagramChatIngestor? _instagram;
        private FacebookChatIngestor? _facebook;

        private ChatOverlayController? _overlay;
        private CancellationTokenSource? _chatCts;

        public MainWindow()
        {
            InitializeComponent();

            // HATA DÜZELTME: GetRequiredService YERİNE 'new' KULLANIYORUZ
            _deviceService = new DeviceService();
            _stream = new StreamController();

            WireUpLogging();

            _settingsVm = new SettingsViewModel(_deviceService);
            _targetsVm = new TargetsViewModel();

            _chatVm.Bind(_chatBus);

            _controlVm = new ControlViewModel(_stream, () => (_targetsVm.Targets, Services.SettingsStore.Load()));

            SetMainContent(new ControlView(_controlVm));
            WireNavigation();

            Loaded += MainWindow_Loaded;
        }

        // ... (WireUpLogging, WireNavigation, SetMainContent, EnsureFfmpegExists AYNI KALSIN) ...
        private void WireUpLogging()
        {
            _stream.OnLog += (s, m) => { if (m.Contains("Error") || m.Contains("Failed")) Log.Error("[Stream] " + m); else Log.Information("[Stream] " + m); };
            _stream.OnExit += (s, c) => { if (c != 0 && c != 255) Log.Warning($"[Stream] Exit Code: {c}"); else Log.Information($"[Stream] Exit Code: {c}"); };
        }

        private void WireNavigation()
        {
            WireNavButton("BtnControl", () => SetMainContent(new ControlView(_controlVm)));
            WireNavButton("BtnTargets", () => SetMainContent(new TargetsView(_targetsVm)));
            WireNavButton("BtnSettings", () => SetMainContent(new SettingsView { DataContext = _settingsVm }));
            WireNavButton("BtnPreview", () => SetMainContent(new PreviewView(new PreviewViewModel(new PreviewService()))));
            WireNavButton("BtnChat", () => SetMainContent(new ChatView { DataContext = _chatVm }));
        }

        private void WireNavButton(string name, Action onClick)
        {
            if (FindName(name) is Button b) b.Click += (_, __) => onClick();
        }

        private void SetMainContent(object content)
        {
            if (FindName("MainContent") is ContentControl cc) cc.Content = content;
        }

        private bool EnsureFfmpegExists()
        {
            try
            {
                var ffmpegPath = UniCast.Encoder.FfmpegProcess.ResolveFfmpegPath();
                if (string.IsNullOrEmpty(ffmpegPath) || !System.IO.File.Exists(ffmpegPath))
                {
                    MessageBox.Show("FFmpeg bulunamadı. Lütfen 'External' klasörünü kontrol edin.", "Eksik Bileşen", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            }
            catch { return false; }
        }
        // ...

        private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                Log.Information("MainWindow yüklendi...");
                if (!EnsureFfmpegExists()) Log.Fatal("FFmpeg yok.");

                _chatCts?.Cancel();
                _chatCts = new CancellationTokenSource();
                var s = Services.SettingsStore.Load();

                if (s.ShowOverlay)
                {
                    var ow = Math.Max(200, (int)s.OverlayWidth);
                    var oh = Math.Max(200, (int)s.OverlayHeight);
                    try
                    {
                        _overlay = new ChatOverlayController(ow, oh, "unicast_overlay");
                        _overlay.Start();
                    }
                    catch (Exception ex) { Log.Error(ex, "Overlay hatası"); }
                }

                StartChatIngestors(s);
            }
            catch (Exception ex) { Log.Error(ex, "Loaded hatası"); }
        }

        private void StartChatIngestors(Core.Settings.SettingsData s)
        {
            var ct = _chatCts!.Token;
            if (!string.IsNullOrWhiteSpace(s.YouTubeApiKey)) { _ytIngestor = new YouTubeChatIngestor(); _ytIngestor.OnMessage += OnMsg; _chatBus.Attach(_ytIngestor); _ = _ytIngestor.StartAsync(ct); }
            if (!string.IsNullOrWhiteSpace(s.TikTokRoomId)) { _tiktok = new TikTokChatIngestor(); _tiktok.OnMessage += OnMsg; _chatBus.Attach(_tiktok); _ = _tiktok.StartAsync(ct); }
            if (!string.IsNullOrWhiteSpace(s.InstagramUserId)) { _instagram = new InstagramChatIngestor(); _instagram.OnMessage += OnMsg; _chatBus.Attach(_instagram); _ = _instagram.StartAsync(ct); }
            if (!string.IsNullOrWhiteSpace(s.FacebookAccessToken)) { _facebook = new FacebookChatIngestor(); _facebook.OnMessage += OnMsg; _chatBus.Attach(_facebook); _ = _facebook.StartAsync(ct); }
        }

        private void OnMsg(ChatMessage msg) { try { _overlay?.Push(msg.Author, msg.Text); } catch { } }

        public void UpdateOverlaySize(double width, double height) { _overlay?.UpdateSize(width, height); }
        public void UpdateOverlayPosition(int x, int y) { _overlay?.UpdatePosition(x, y); }
        public void RefreshOverlay() { _overlay?.ReloadSettings(); }

        // Mola Metotları
        public void StartBreak(int minutes) { _overlay?.StartBreakMode(minutes); }
        public void StopBreak() { _overlay?.StopBreakMode(); }

        protected override void OnClosed(EventArgs e)
        {
            try { _chatCts?.Cancel(); } catch { }

            // Servisleri durdur
            try { _ytIngestor?.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { _tiktok?.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { _instagram?.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { _facebook?.StopAsync().GetAwaiter().GetResult(); } catch { }

            try { if (_overlay != null) _overlay.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            _chatBus.Dispose();
            try { (_stream as IAsyncDisposable)?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }

            _controlVm.Dispose(); // ViewModel'i temizle
            base.OnClosed(e);
        }
    }
}