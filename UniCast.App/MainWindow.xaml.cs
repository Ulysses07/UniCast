using System;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using UniCast.App.Overlay;
using UniCast.App.Services;
using UniCast.App.Services.Capture;
using UniCast.App.Services.Chat;   // YouTubeChatIngestor, TikTokChatIngestor, InstagramChatIngestor, FacebookChatIngestor
using UniCast.App.ViewModels;
using UniCast.App.Views;
using UniCast.Core.Chat;

namespace UniCast.App
{
    public partial class MainWindow : Window
    {
        private readonly IDeviceService _deviceService;
        private readonly IStreamController _stream;
        private readonly SettingsViewModel _settingsVm;
        private readonly TargetsViewModel _targetsVm;
        private readonly ControlViewModel _controlVm;

        // Chat çekirdekleri
        private readonly ChatBus _chatBus = new();
        private readonly ChatViewModel _chatVm = new();

        // Ingestorlar
        private YouTubeChatIngestor? _ytIngestor;
        private TikTokChatIngestor? _tiktok;
        private InstagramChatIngestor? _instagram;
        private FacebookChatIngestor? _facebook;

        // Overlay
        private ChatOverlayController? _overlay;

        private CancellationTokenSource? _chatCts;

        public MainWindow()
        {
            InitializeComponent();

            _deviceService = new DeviceService();
            _stream = new StreamController();

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

            // Varsayılan ekran
            if (FindName("MainContent") is ContentControl cc)
                cc.Content = new ControlView(_controlVm);

            // Navigasyon butonları (yoksa sessiz geç)
            WireNavButton("BtnControl", () =>
            {
                if (FindName("MainContent") is ContentControl c)
                    c.Content = new ControlView(_controlVm);
            });

            WireNavButton("BtnTargets", () =>
            {
                if (FindName("MainContent") is ContentControl c)
                    c.Content = new TargetsView(_targetsVm);
            });

            WireNavButton("BtnSettings", () =>
            {
                if (FindName("MainContent") is ContentControl c)
                    c.Content = new SettingsView { DataContext = _settingsVm };
            });

            WireNavButton("BtnPreview", () =>
            {
                if (FindName("MainContent") is ContentControl c)
                    c.Content = new PreviewView(new PreviewViewModel());
            });

            WireNavButton("BtnChat", () =>
            {
                if (FindName("MainContent") is ContentControl c)
                    c.Content = new ChatView { DataContext = _chatVm };
            });
        }


            // Overlay'i Settings çözünürlüğü ile başlat
            /*try
            {
                var s = Services.SettingsStore.Load();
                var ow = Math.Max(320, s.Width);
                var oh = Math.Max(240, s.Height);
                _overlay = new ChatOverlayController(width: ow, height: oh, pipeName: "unicast_overlay");
                _overlay.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Overlay start error: " + ex.Message);
            }

            Loaded += MainWindow_Loaded;
        }*/

        private void WireNavButton(string name, Action onClick)
        {
            if (FindName(name) is System.Windows.Controls.Button b)
                b.Click += (_, __) => onClick();
        }

        private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                _chatCts?.Cancel();
                _chatCts = new CancellationTokenSource();

                var s = Services.SettingsStore.Load();

                // YouTube – ayarlar doluysa başlat ve overlay'e bağla
                if (!string.IsNullOrWhiteSpace(s.YouTubeApiKey) &&
                    !string.IsNullOrWhiteSpace(s.YouTubeChannelId))
                {
                    _ytIngestor = new YouTubeChatIngestor(new HttpClient());
                    _ytIngestor.OnMessage += OnIngestorMessageToOverlay;
                    _chatBus.Attach(_ytIngestor);
                    await _ytIngestor.StartAsync(_chatCts.Token);
                }

                // TikTok – ayarlar doluysa başlat ve overlay'e bağla
                if (!string.IsNullOrWhiteSpace(s.TikTokRoomId))
                {
                    _tiktok = new TikTokChatIngestor(new HttpClient());
                    _tiktok.OnMessage += OnIngestorMessageToOverlay;
                    _chatBus.Attach(_tiktok);
                    await _tiktok.StartAsync(_chatCts.Token);
                }

                // Instagram – ayarlar doluysa başlat ve overlay'e bağla
                if (!string.IsNullOrWhiteSpace(s.InstagramUserId) &&
                    !string.IsNullOrWhiteSpace(s.InstagramSessionId))
                {
                    _instagram = new InstagramChatIngestor(new HttpClient());
                    _instagram.OnMessage += OnIngestorMessageToOverlay;
                    _chatBus.Attach(_instagram);
                    await _instagram.StartAsync(_chatCts.Token);
                }

                // Facebook – ayarlar doluysa başlat ve overlay'e bağla
                if (!string.IsNullOrWhiteSpace(s.FacebookAccessToken) &&
                    !string.IsNullOrWhiteSpace(s.FacebookPageId))
                {
                    _facebook = new FacebookChatIngestor(new HttpClient());
                    _facebook.OnMessage += OnIngestorMessageToOverlay;
                    _chatBus.Attach(_facebook);
                    await _facebook.StartAsync(_chatCts.Token);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Chat start error: " + ex.Message);
            }
        }

        // Tüm ingestörlerden gelen mesajı overlay'e bas
        private void OnIngestorMessageToOverlay(ChatMessage msg)
        {
            try { _overlay?.Push(msg.Author, msg.Text); } catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _chatCts?.Cancel(); } catch { }

            // Ingestör event unsub
            if (_ytIngestor != null) _ytIngestor.OnMessage -= OnIngestorMessageToOverlay;
            if (_tiktok != null) _tiktok.OnMessage -= OnIngestorMessageToOverlay;
            if (_instagram != null) _instagram.OnMessage -= OnIngestorMessageToOverlay;
            if (_facebook != null) _facebook.OnMessage -= OnIngestorMessageToOverlay;

            // Kibar kapanış
            TryStop(_ytIngestor);
            TryStop(_tiktok);
            TryStop(_instagram);
            TryStop(_facebook);

            // Overlay kapat
            try { if (_overlay != null) _overlay.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }

            _chatBus.Dispose();

            base.OnClosed(e);

            static void TryStop(IChatIngestor? ing)
            {
                try { ing?.StopAsync().GetAwaiter().GetResult(); } catch { }
                try { (ing as IAsyncDisposable)?.DisposeAsync().GetAwaiter().GetResult(); } catch { }
            }
        }
    }
}
