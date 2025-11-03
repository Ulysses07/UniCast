using System;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

using UniCast.App.Services;
using UniCast.App.Services.Chat;   // YouTubeChatIngestor, TikTokChatIngestor, InstagramChatIngestor, FacebookChatIngestor
using UniCast.App.ViewModels;
using UniCast.App.Views;

using UniCast.Core.Chat;
using UniCast.Core.Settings;

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

            Loaded += MainWindow_Loaded;
        }

        private void WireNavButton(string name, Action onClick)
        {
            if (FindName(name) is System.Windows.Controls.Button b)
                b.Click += (_, __) => onClick();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _chatCts?.Cancel();
                _chatCts = new CancellationTokenSource();

                var s = Services.SettingsStore.Load();

                // YouTube – ayarlar doluysa başlat (özellik atamadan!)
                if (!string.IsNullOrWhiteSpace(s.YouTubeApiKey) &&
                    !string.IsNullOrWhiteSpace(s.YouTubeChannelId))
                {
                    _ytIngestor = new YouTubeChatIngestor(new HttpClient());
                    _chatBus.Attach(_ytIngestor);
                    await _ytIngestor.StartAsync(_chatCts.Token);
                }

                // TikTok
                if (!string.IsNullOrWhiteSpace(s.TikTokRoomId))
                {
                    _tiktok = new TikTokChatIngestor(new HttpClient());
                    _chatBus.Attach(_tiktok);
                    await _tiktok.StartAsync(_chatCts.Token);
                }

                // Instagram
                if (!string.IsNullOrWhiteSpace(s.InstagramUserId) &&
                    !string.IsNullOrWhiteSpace(s.InstagramSessionId))
                {
                    _instagram = new InstagramChatIngestor(new HttpClient());
                    _chatBus.Attach(_instagram);
                    await _instagram.StartAsync(_chatCts.Token);
                }

                // Facebook
                if (!string.IsNullOrWhiteSpace(s.FacebookAccessToken) &&
                    !string.IsNullOrWhiteSpace(s.FacebookPageId))
                {
                    _facebook = new FacebookChatIngestor(new HttpClient());
                    _chatBus.Attach(_facebook);
                    await _facebook.StartAsync(_chatCts.Token);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Chat start error: " + ex.Message);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _chatCts?.Cancel(); } catch { }

            // Kibar kapanış
            TryStop(_ytIngestor);
            TryStop(_tiktok);
            TryStop(_instagram);
            TryStop(_facebook);

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
