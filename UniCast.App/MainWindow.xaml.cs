using System;
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
using MessageBox = System.Windows.MessageBox;

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
            WireNavButton("BtnPreview", () => SetMainContent(new PreviewView(new PreviewViewModel())));
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
            try
            {
                Log.Information("MainWindow yüklendi...");

                if (!EnsureFfmpegExists())
                {
                    Log.Fatal("FFmpeg yok.");
                }

                // DÜZELTME: Eski CTS'i dispose et
                _chatCts?.Cancel();
                _chatCts?.Dispose();
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
                        Log.Information($"Overlay başlatıldı: {ow}x{oh}");
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

            // DÜZELTME: HttpClient artık factory'den alınıyor (new HttpClient() YOK!)
            if (!string.IsNullOrWhiteSpace(s.YouTubeApiKey))
            {
                _ytIngestor = new YouTubeChatIngestor(); // Factory kullanır
                _ytIngestor.OnMessage += OnMsg;
                _chatBus.Attach(_ytIngestor);
                _ = _ytIngestor.StartAsync(ct);
            }

            if (!string.IsNullOrWhiteSpace(s.TikTokRoomId))
            {
                _tiktok = new TikTokChatIngestor(); // Factory kullanır
                _tiktok.OnMessage += OnMsg;
                _chatBus.Attach(_tiktok);
                _ = _tiktok.StartAsync(ct);
            }

            if (!string.IsNullOrWhiteSpace(s.InstagramUserId))
            {
                _instagram = new InstagramChatIngestor(); // Factory kullanır
                _instagram.OnMessage += OnMsg;
                _chatBus.Attach(_instagram);
                _ = _instagram.StartAsync(ct);
            }

            if (!string.IsNullOrWhiteSpace(s.FacebookAccessToken))
            {
                _facebook = new FacebookChatIngestor(); // Factory kullanır
                _facebook.OnMessage += OnMsg;
                _chatBus.Attach(_facebook);
                _ = _facebook.StartAsync(ct);
            }
        }

        public void StartBreak(int minutes)
        {
            if (_overlay != null)
            {
                _overlay.StartBreakMode(minutes);
                Log.Information($"Mola modu başlatıldı: {minutes} dakika");
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
            try { _overlay?.Push(msg.Author, msg.Text); } catch { }
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

        protected override void OnClosed(EventArgs e)
        {
            // DÜZELTME: Düzgün cleanup
            try
            {
                _chatCts?.Cancel();
            }
            catch { }

            // Chat ingestor'ları durdur
            try { _ytIngestor?.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { _tiktok?.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { _instagram?.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { _facebook?.StopAsync().GetAwaiter().GetResult(); } catch { }

            // Overlay'i kapat
            try
            {
                _overlay?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch { }

            // ChatBus'ı dispose et
            _chatBus.Dispose();

            // Stream controller'ı dispose et
            try
            {
                (_stream as IAsyncDisposable)?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch { }

            // ControlViewModel'i dispose et
            _controlVm.Dispose();

            // CTS'i dispose et
            try
            {
                _chatCts?.Dispose();
            }
            catch { }

            base.OnClosed(e);
        }
    }
}