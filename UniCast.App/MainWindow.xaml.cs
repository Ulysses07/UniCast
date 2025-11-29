using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using UniCast.App.Infrastructure;
using UniCast.App.Overlay;
using UniCast.App.Services;
using UniCast.App.Services.Capture;
using UniCast.App.Services.Chat;
using UniCast.App.ViewModels;
using UniCast.App.Views;
using UniCast.Core.Chat;
using Serilog;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace UniCast.App
{
    public partial class MainWindow : Window
    {
        // DI'dan alınan servisler
        private readonly IDeviceService _deviceService;
        private readonly IStreamController _stream;
        private readonly ChatBus _chatBus;

        // ViewModels
        private readonly SettingsViewModel _settingsVm;
        private readonly TargetsViewModel _targetsVm;
        private readonly ControlViewModel _controlVm;
        private readonly ChatViewModel _chatVm;

        // Chat Ingestors
        private YouTubeChatIngestor? _ytIngestor;
        private TikTokChatIngestor? _tiktok;
        private InstagramChatIngestor? _instagram;
        private FacebookChatIngestor? _facebook;

        // Overlay
        private ChatOverlayController? _overlay;
        private CancellationTokenSource? _chatCts;

        // DÜZELTME: Çift yükleme önleme
        private bool _isLoaded = false;

        public MainWindow()
        {
            InitializeComponent();

            // DI Container'dan servisleri al
            var services = App.Services;

            _deviceService = services.GetRequiredService<IDeviceService>();
            _stream = services.GetRequiredService<IStreamController>();
            _chatBus = services.GetRequiredService<ChatBus>();

            // ViewModels
            _settingsVm = services.GetRequiredService<SettingsViewModel>();
            _targetsVm = services.GetRequiredService<TargetsViewModel>();
            _controlVm = services.GetRequiredService<ControlViewModel>();
            _chatVm = services.GetRequiredService<ChatViewModel>();

            // ChatViewModel'i ChatBus'a bağla
            _chatVm.Bind(_chatBus);

            WireUpLogging();
            SetMainContent(new ControlView(_controlVm));
            WireNavigation();

            Loaded += MainWindow_Loaded;
        }

        private void WireUpLogging()
        {
            _stream.OnLog += (s, m) =>
            {
                if (m.Contains("Error") || m.Contains("Failed"))
                    Log.Error("[Stream] " + m);
                else
                    Log.Information("[Stream] " + m);
            };

            _stream.OnExit += (s, c) =>
            {
                if (c != 0 && c != 255)
                    Log.Warning("[Stream] Exit Code: {ExitCode}", c);
                else
                    Log.Information("[Stream] Exit Code: {ExitCode}", c);
            };
        }

        private void WireNavigation()
        {
            WireNavButton("BtnControl", () => SetMainContent(new ControlView(_controlVm)));
            WireNavButton("BtnTargets", () => SetMainContent(new TargetsView(_targetsVm)));
            WireNavButton("BtnSettings", () => SetMainContent(new SettingsView { DataContext = _settingsVm }));
            WireNavButton("BtnPreview", () => SetMainContent(new PreviewView(App.Services.GetRequiredService<PreviewViewModel>())));
            WireNavButton("BtnChat", () => SetMainContent(new ChatView { DataContext = _chatVm }));
        }

        private void WireNavButton(string name, Action onClick)
        {
            if (FindName(name) is Button b)
                b.Click += (_, __) => onClick();
        }

        private void SetMainContent(object content)
        {
            if (FindName("MainContent") is ContentControl cc)
                cc.Content = content;
        }

        private bool EnsureFfmpegExists()
        {
            try
            {
                var ffmpegPath = UniCast.Encoder.FfmpegProcess.ResolveFfmpegPath();
                if (string.IsNullOrEmpty(ffmpegPath) || !System.IO.File.Exists(ffmpegPath))
                {
                    var result = MessageBox.Show(
                        "Yayın için 'ffmpeg.exe' dosyası bulunamadı.\nİndirme sayfasını açmak ister misiniz?",
                        "Kritik Eksik", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                    if (result == MessageBoxResult.Yes)
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "https://www.gyan.dev/ffmpeg/builds/",
                            UseShellExecute = true
                        });
                    }
                    return false;
                }
                return true;
            }
            catch { return false; }
        }

        private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            // DÜZELTME: Çift yükleme önleme
            if (_isLoaded) return;
            _isLoaded = true;

            try
            {
                Log.Information("MainWindow yüklendi...");

                if (!EnsureFfmpegExists())
                {
                    Log.Fatal("FFmpeg yok.");
                }

                // CTS oluştur
                _chatCts?.Cancel();
                _chatCts?.Dispose();
                _chatCts = new CancellationTokenSource();

                var s = SettingsStore.Load();

                // Overlay başlat
                if (s.ShowOverlay)
                {
                    StartOverlay(s);
                }

                // Chat ingestors başlat
                StartChatIngestors(s);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MainWindow_Loaded hatası");
            }
        }

        private void StartOverlay(Core.Settings.SettingsData s)
        {
            var ow = Math.Max(Constants.Overlay.MinWidth, (int)s.OverlayWidth);
            var oh = Math.Max(Constants.Overlay.MinHeight, (int)s.OverlayHeight);

            try
            {
                // DÜZELTME: Constants kullanımı
                _overlay = new ChatOverlayController(ow, oh, "unicast-chat-overlay");
                _overlay.Start();
                Log.Information("Overlay başlatıldı: {Width}x{Height}", ow, oh);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Overlay başlatma hatası");
            }
        }

        private void StartChatIngestors(Core.Settings.SettingsData s)
        {
            var ct = _chatCts!.Token;

            // YouTube
            if (!string.IsNullOrWhiteSpace(s.YouTubeApiKey))
            {
                _ytIngestor = App.Services.GetRequiredService<YouTubeChatIngestor>();
                _ytIngestor.OnMessage += OnMsg;
                _chatBus.Attach(_ytIngestor);

                _ytIngestor.StartAsync(ct)
                    .SafeFireAndForget("YouTube Chat", ex => Log.Error(ex, "YouTube Chat hatası"));
            }

            // TikTok
            if (!string.IsNullOrWhiteSpace(s.TikTokRoomId))
            {
                _tiktok = App.Services.GetRequiredService<TikTokChatIngestor>();
                _tiktok.OnMessage += OnMsg;
                _chatBus.Attach(_tiktok);

                _tiktok.StartAsync(ct)
                    .SafeFireAndForget("TikTok Chat", ex => Log.Error(ex, "TikTok Chat hatası"));
            }

            // Instagram
            if (!string.IsNullOrWhiteSpace(s.InstagramUserId))
            {
                _instagram = App.Services.GetRequiredService<InstagramChatIngestor>();
                _instagram.OnMessage += OnMsg;
                _chatBus.Attach(_instagram);

                _instagram.StartAsync(ct)
                    .SafeFireAndForget("Instagram Chat", ex => Log.Error(ex, "Instagram Chat hatası"));
            }

            // Facebook
            if (!string.IsNullOrWhiteSpace(s.FacebookAccessToken))
            {
                _facebook = App.Services.GetRequiredService<FacebookChatIngestor>();
                _facebook.OnMessage += OnMsg;
                _chatBus.Attach(_facebook);

                _facebook.StartAsync(ct)
                    .SafeFireAndForget("Facebook Chat", ex => Log.Error(ex, "Facebook Chat hatası"));
            }
        }

        public void StartBreak(int minutes)
        {
            if (_overlay != null)
            {
                _overlay.StartBreakMode(minutes);
                Log.Information("Mola modu başlatıldı: {Minutes} dakika", minutes);
            }
        }

        public void StopBreak()
        {
            if (_overlay != null)
            {
                _overlay.StopBreakMode();
                Log.Information("Mola modu sonlandırıldı.");
            }
        }

        private void OnMsg(ChatMessage msg)
        {
            try
            {
                _overlay?.Push(msg.Author, msg.Text);
            }
            catch (Exception ex)
            {
                Log.Debug("Overlay push hatası: {Message}", ex.Message);
            }
        }

        public void UpdateOverlaySize(double width, double height)
        {
            _overlay?.UpdateSize(width, height);
        }

        public void UpdateOverlayPosition(int x, int y)
        {
            _overlay?.UpdatePosition(x, y);
        }

        public void RefreshOverlay()
        {
            _overlay?.ReloadSettings();
        }

        protected override async void OnClosed(EventArgs e)
        {
            Log.Information("MainWindow kapatılıyor...");

            // 1. CTS iptal
            try { _chatCts?.Cancel(); } catch { }

            // 2. Event handler'ları ÖNCE kaldır (Memory Leak Fix)
            if (_ytIngestor != null) _ytIngestor.OnMessage -= OnMsg;
            if (_tiktok != null) _tiktok.OnMessage -= OnMsg;
            if (_instagram != null) _instagram.OnMessage -= OnMsg;
            if (_facebook != null) _facebook.OnMessage -= OnMsg;

            // 3. Chat ingestors durdur
            await StopIngestorSafe(_ytIngestor, "YouTube");
            await StopIngestorSafe(_tiktok, "TikTok");
            await StopIngestorSafe(_instagram, "Instagram");
            await StopIngestorSafe(_facebook, "Facebook");

            // 4. ChatBus'tan ayır
            if (_ytIngestor != null) _chatBus.Detach(_ytIngestor);
            if (_tiktok != null) _chatBus.Detach(_tiktok);
            if (_instagram != null) _chatBus.Detach(_instagram);
            if (_facebook != null) _chatBus.Detach(_facebook);

            // 5. Overlay kapat
            try
            {
                if (_overlay != null)
                    await _overlay.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Debug("Overlay dispose hatası: {Message}", ex.Message);
            }

            // 6. DÜZELTME: StreamController'ı DISPOSE ETMİYORUZ
            // DI Container App.OnExit'te halledecek - çift dispose önleme

            // 7. ControlViewModel dispose
            _controlVm.Dispose();

            // 8. ChatViewModel dispose
            _chatVm.Dispose();

            // 9. CTS dispose
            try { _chatCts?.Dispose(); } catch { }

            base.OnClosed(e);
        }

        private async System.Threading.Tasks.Task StopIngestorSafe(IChatIngestor? ingestor, string name)
        {
            if (ingestor == null) return;

            try
            {
                await ingestor.StopAsync();
                Log.Debug("{Name} ingestor durduruldu", name);
            }
            catch (Exception ex)
            {
                Log.Debug("{Name} ingestor durdurma hatası: {Message}", name, ex.Message);
            }
        }
    }
}