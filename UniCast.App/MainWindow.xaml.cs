using System;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using UniCast.App.Services;
using UniCast.App.Services.Chat;
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

            // Navigasyon butonları
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
                _chatCts = new CancellationTokenSource();

                // ✅ YouTube Chat
                try
                {
                    _ytIngestor = new YouTubeChatIngestor(new HttpClient());
                    _chatBus.Attach(_ytIngestor);
                    await _ytIngestor.StartAsync(_chatCts.Token);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("YouTube chat start error: " + ex.Message);
                }

                // ✅ TikTok Chat
                try
                {
                    _tiktok = new TikTokChatIngestor(new HttpClient());
                    _chatBus.Attach(_tiktok);
                    await _tiktok.StartAsync(_chatCts.Token);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("TikTok chat start error: " + ex.Message);
                }

                // ✅ Instagram Chat
                try
                {
                    _instagram = new InstagramChatIngestor(new HttpClient());
                    _chatBus.Attach(_instagram);
                    await _instagram.StartAsync(_chatCts.Token);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Instagram chat start error: " + ex.Message);
                }
                // ✅ Facebook Chat
                try
                {
                    _facebook = new FacebookChatIngestor(new HttpClient());
                    _chatBus.Attach(_facebook);
                    await _facebook.StartAsync(_chatCts.Token);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Facebook chat start error: " + ex.Message);
                }

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Chat global start error: " + ex.Message);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _chatCts?.Cancel(); } catch { }

            try { _ytIngestor?.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { _tiktok?.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { _instagram?.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { _facebook?.StopAsync().GetAwaiter().GetResult(); } catch { }

            _chatBus.Dispose();

            base.OnClosed(e);
        }
    }
}
