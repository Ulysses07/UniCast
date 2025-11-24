using System;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls; // WPF Kontrolleri
using UniCast.App.Overlay;
using UniCast.App.Services;
using UniCast.App.Services.Capture;
using UniCast.App.Services.Chat;
using UniCast.App.ViewModels;
using UniCast.App.Views;
using UniCast.Core.Chat;
using Serilog;

namespace UniCast.App
{
    public partial class MainWindow : Window
    {
        // --- Servisler ve ViewModel'ler ---
        private readonly IDeviceService _deviceService;
        private readonly IStreamController _stream;
        private readonly SettingsViewModel _settingsVm;
        private readonly TargetsViewModel _targetsVm;
        private readonly ControlViewModel _controlVm;

        // --- Chat Bileşenleri ---
        private readonly ChatBus _chatBus = new();
        private readonly ChatViewModel _chatVm = new();

        // --- Chat Ingestor'ları ---
        private YouTubeChatIngestor? _ytIngestor;
        private TikTokChatIngestor? _tiktok;
        private InstagramChatIngestor? _instagram;
        private FacebookChatIngestor? _facebook;

        // --- Overlay ---
        private ChatOverlayController? _overlay;

        // --- Token Kaynağı ---
        private CancellationTokenSource? _chatCts;

        public MainWindow()
        {
            InitializeComponent();

            // 1. Servisleri Oluştur
            _deviceService = new DeviceService();
            _stream = new StreamController();

            // 2. Loglama Bağlantıları (Wiring)
            WireUpLogging();

            // 3. ViewModel'leri Hazırla
            _settingsVm = new SettingsViewModel(_deviceService);
            _targetsVm = new TargetsViewModel();

            _chatVm.Bind(_chatBus);

            _controlVm = new ControlViewModel(
                _stream,
                () =>
                {
                    var settings = Services.SettingsStore.Load();
                    return (_targetsVm.Targets, settings);
                });

            // 4. Varsayılan Ekranı Ayarla (ControlView)
            SetMainContent(new ControlView(_controlVm));

            // 5. Navigasyon Butonlarını Bağla
            WireNavigation();

            // 6. Yükleme Tamamlandığında Chat ve Overlay'i Başlat
            Loaded += MainWindow_Loaded;
        }

        // --- Loglama Bağlantısı ---
        private void WireUpLogging()
        {
            _stream.OnLog += (sender, message) =>
            {
                if (message.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("Failed", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Error("[Stream] {LogMessage}", message);
                }
                else
                {
                    Log.Information("[Stream] {LogMessage}", message);
                }
            };

            _stream.OnExit += (sender, code) =>
            {
                if (code != 0 && code != 255)
                    Log.Warning("[Stream] FFmpeg beklenmedik çıkış kodu: {ExitCode}", code);
                else
                    Log.Information("[Stream] FFmpeg kapandı. Kod: {ExitCode}", code);
            };
        }

        // --- Navigasyon ---
        private void WireNavigation()
        {
            WireNavButton("BtnControl", () => SetMainContent(new ControlView(_controlVm)));
            WireNavButton("BtnTargets", () => SetMainContent(new TargetsView(_targetsVm)));
            WireNavButton("BtnSettings", () => SetMainContent(new SettingsView { DataContext = _settingsVm }));
            WireNavButton("BtnPreview", () => SetMainContent(new PreviewView(new PreviewViewModel())));
            WireNavButton("BtnChat", () => SetMainContent(new ChatView { DataContext = _chatVm }));
        }

        private void WireNavButton(string name, Action onClick)
        {
            // HATA DÜZELTME: Button'un WPF butonu olduğunu açıkça belirttik (System.Windows.Controls.Button)
            if (FindName(name) is System.Windows.Controls.Button b)
                b.Click += (_, __) => onClick();
        }

        private void SetMainContent(object content)
        {
            if (FindName("MainContent") is ContentControl cc)
                cc.Content = content;
        }

        // --- Uygulama Yüklendiğinde (Startup Logic) ---
        // UYARI DÜZELTME: 'async' kaldırıldı çünkü içeride await kullanılmıyor (fire-and-forget)
        private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                Log.Information("MainWindow yüklendi, servisler başlatılıyor...");

                _chatCts?.Cancel();
                _chatCts = new CancellationTokenSource();

                var s = Services.SettingsStore.Load();

                // 1. Overlay Başlatma
                if (s.ShowOverlay)
                {
                    var ow = Math.Max(320, s.Width);
                    var oh = Math.Max(240, s.Height);
                    try
                    {
                        _overlay = new ChatOverlayController(width: ow, height: oh, pipeName: "unicast_overlay");
                        _overlay.Start();
                        Log.Information("Overlay başlatıldı: {Width}x{Height}", ow, oh);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Overlay başlatılamadı.");
                    }
                }

                // 2. Chat Ingestor'ları Başlatma (Fire-and-forget)
                // Arka planda çalışacakları için 'await' kullanmadan _ = Task yapıyoruz.

                // YouTube
                if (!string.IsNullOrWhiteSpace(s.YouTubeApiKey) && !string.IsNullOrWhiteSpace(s.YouTubeChannelId))
                {
                    Log.Information("YouTube Chat başlatılıyor...");
                    _ytIngestor = new YouTubeChatIngestor(new HttpClient());
                    _ytIngestor.OnMessage += OnIngestorMessageToOverlay;
                    _chatBus.Attach(_ytIngestor);
                    _ = _ytIngestor.StartAsync(_chatCts.Token);
                }

                // TikTok
                if (!string.IsNullOrWhiteSpace(s.TikTokRoomId))
                {
                    Log.Information("TikTok Chat başlatılıyor...");
                    _tiktok = new TikTokChatIngestor(new HttpClient());
                    _tiktok.OnMessage += OnIngestorMessageToOverlay;
                    _chatBus.Attach(_tiktok);
                    _ = _tiktok.StartAsync(_chatCts.Token);
                }

                // Instagram
                if (!string.IsNullOrWhiteSpace(s.InstagramUserId) && !string.IsNullOrWhiteSpace(s.InstagramSessionId))
                {
                    Log.Information("Instagram Chat başlatılıyor...");
                    _instagram = new InstagramChatIngestor(new HttpClient());
                    _instagram.OnMessage += OnIngestorMessageToOverlay;
                    _chatBus.Attach(_instagram);
                    _ = _instagram.StartAsync(_chatCts.Token);
                }

                // Facebook
                if (!string.IsNullOrWhiteSpace(s.FacebookAccessToken) && !string.IsNullOrWhiteSpace(s.FacebookPageId))
                {
                    Log.Information("Facebook Chat başlatılıyor...");
                    _facebook = new FacebookChatIngestor(new HttpClient());
                    _facebook.OnMessage += OnIngestorMessageToOverlay;
                    _chatBus.Attach(_facebook);
                    _ = _facebook.StartAsync(_chatCts.Token);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MainWindow_Loaded sırasında hata oluştu.");
            }
        }
        public void UpdateOverlayPosition(int x, int y)
        {
            if (_overlay != null)
            {
                _overlay.UpdatePosition(x, y);
                Log.Information("Overlay konumu güncellendi: {X}, {Y}", x, y);
            }
        }

        // --- Mesajları Overlay'e İlet ---
        private void OnIngestorMessageToOverlay(ChatMessage msg)
        {
            try
            {
                _overlay?.Push(msg.Author, msg.Text);
            }
            catch
            {
                // Overlay hatası chat akışını bozmamalı
            }
        }

        // --- Uygulama Kapanırken Temizlik ---
        protected override void OnClosed(EventArgs e)
        {
            Log.Information("MainWindow kapatılıyor, kaynaklar serbest bırakılıyor...");

            try { _chatCts?.Cancel(); } catch { }

            // Event Aboneliklerini Kaldır
            if (_ytIngestor != null) _ytIngestor.OnMessage -= OnIngestorMessageToOverlay;
            if (_tiktok != null) _tiktok.OnMessage -= OnIngestorMessageToOverlay;
            if (_instagram != null) _instagram.OnMessage -= OnIngestorMessageToOverlay;
            if (_facebook != null) _facebook.OnMessage -= OnIngestorMessageToOverlay;

            // Ingestor'ları Durdur
            TryStop(_ytIngestor);
            TryStop(_tiktok);
            TryStop(_instagram);
            TryStop(_facebook);

            // Overlay Kapat
            try
            {
                if (_overlay != null)
                    _overlay.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch { }

            _chatBus.Dispose();

            // Stream Controller Dispose
            try
            {
                (_stream as IAsyncDisposable)?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch { }

            base.OnClosed(e);

            static void TryStop(IChatIngestor? ing)
            {
                try { ing?.StopAsync().GetAwaiter().GetResult(); } catch { }
                try { (ing as IAsyncDisposable)?.DisposeAsync().GetAwaiter().GetResult(); } catch { }
            }
        }
    }
}