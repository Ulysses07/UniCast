using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using UniCast.Core.Chat.Ingestors;
using UserControl = System.Windows.Controls.UserControl;

namespace UniCast.App.Views
{
    /// <summary>
    /// Facebook Live Chat için WebView2 Host kontrolü.
    /// Arka planda çalışır ve chat yorumlarını scrape eder.
    /// 
    /// Kullanım:
    /// 1. XAML'a ekle veya code-behind'da oluştur
    /// 2. Initialize() çağır
    /// 3. StartChatAsync() ile chat'i başlat
    /// </summary>
    public class FacebookChatHost : UserControl, IDisposable
    {
        private WebView2? _webView;
        private FacebookChatScraper? _scraper;
        private bool _isInitialized;
        private bool _disposed;

        private Action<string>? _messageHandler;

        /// <summary>
        /// Chat scraper instance'ı.
        /// </summary>
        public FacebookChatScraper? Scraper => _scraper;

        /// <summary>
        /// WebView2 yüklendi mi?
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Facebook cookie'leri (login'den alınır).
        /// </summary>
        public string? Cookies { get; set; }

        public FacebookChatHost()
        {
            // WebView2 gizli olacak
            Visibility = Visibility.Collapsed;
            Width = 0;
            Height = 0;
        }

        /// <summary>
        /// WebView2'yi başlatır.
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized || _disposed)
                return;

            try
            {
                Log.Debug("[FacebookChatHost] WebView2 başlatılıyor...");

                _webView = new WebView2
                {
                    Width = 1,
                    Height = 1,
                    Visibility = Visibility.Collapsed
                };

                Content = _webView;

                // WebView2 ortamını oluştur
                var userDataFolder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UniCast", "WebView2_Chat");

                System.IO.Directory.CreateDirectory(userDataFolder);

                var env = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: userDataFolder);

                await _webView.EnsureCoreWebView2Async(env);

                // Ayarlar
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

                // Ses ve video'yu kapat (kaynak tasarrufu)
                _webView.CoreWebView2.Settings.IsWebMessageEnabled = true;

                _isInitialized = true;
                Log.Information("[FacebookChatHost] WebView2 hazır");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FacebookChatHost] WebView2 başlatma hatası");
                throw;
            }
        }

        /// <summary>
        /// Facebook Live chat'i başlatır.
        /// </summary>
        /// <param name="liveVideoUrl">Live video URL veya ID</param>
        /// <param name="cookies">Facebook cookie'leri (opsiyonel, property'den de alınabilir)</param>
        public async Task<FacebookChatScraper> StartChatAsync(string liveVideoUrl, string? cookies = null)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FacebookChatHost));

            if (!_isInitialized)
                await InitializeAsync();

            if (_webView?.CoreWebView2 == null)
                throw new InvalidOperationException("WebView2 başlatılamadı");

            // Cookie'leri ayarla
            var effectiveCookies = cookies ?? Cookies;
            if (!string.IsNullOrEmpty(effectiveCookies))
            {
                SetCookies(effectiveCookies);
            }

            // Mevcut scraper'ı durdur
            if (_scraper != null)
            {
                await _scraper.StopAsync();
                _scraper.Dispose();
            }

            // Yeni scraper oluştur
            _scraper = new FacebookChatScraper(liveVideoUrl);
            _scraper.Cookies = effectiveCookies;

            // WebView2 kontrollerini ayarla
            _scraper.SetWebViewControls(
                ensureReady: EnsureWebViewReadyAsync,
                navigate: NavigateAsync,
                executeScript: ExecuteScriptAsync,
                registerHandler: RegisterMessageHandler,
                unregisterHandler: UnregisterMessageHandler
            );

            // Başlat
            await _scraper.StartAsync();

            return _scraper;
        }

        /// <summary>
        /// Chat'i durdurur.
        /// </summary>
        public async Task StopChatAsync()
        {
            if (_scraper != null)
            {
                await _scraper.StopAsync();
                _scraper.Dispose();
                _scraper = null;
            }
        }

        private void SetCookies(string cookies)
        {
            if (_webView?.CoreWebView2 == null)
                return;

            try
            {
                var cookieManager = _webView.CoreWebView2.CookieManager;

                // Cookie string'i parse et
                var cookiePairs = cookies.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var pair in cookiePairs)
                {
                    var parts = pair.Trim().Split(new[] { '=' }, 2);
                    if (parts.Length == 2)
                    {
                        var name = parts[0].Trim();
                        var value = parts[1].Trim();

                        var cookie = cookieManager.CreateCookie(name, value, ".facebook.com", "/");
                        cookie.IsSecure = true;
                        cookie.IsHttpOnly = true;

                        cookieManager.AddOrUpdateCookie(cookie);
                    }
                }

                Log.Debug("[FacebookChatHost] Cookie'ler ayarlandı");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[FacebookChatHost] Cookie ayarlama hatası");
            }
        }

        #region WebView2 Kontrolleri

        private Task EnsureWebViewReadyAsync()
        {
            if (_webView?.CoreWebView2 != null)
                return Task.CompletedTask;

            return InitializeAsync();
        }

        private async Task NavigateAsync(string url)
        {
            if (_webView?.CoreWebView2 == null)
                return;

            var tcs = new TaskCompletionSource<bool>();

            void OnNavigationCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                tcs.TrySetResult(e.IsSuccess);
            }

            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.Navigate(url);

            await tcs.Task;
        }

        private async Task<string> ExecuteScriptAsync(string script)
        {
            if (_webView?.CoreWebView2 == null)
                return "";

            return await _webView.CoreWebView2.ExecuteScriptAsync(script);
        }

        private void RegisterMessageHandler(Action<string> handler)
        {
            if (_webView?.CoreWebView2 == null)
                return;

            _messageHandler = handler;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        }

        private void UnregisterMessageHandler()
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            }
            _messageHandler = null;
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var message = e.WebMessageAsJson;
                _messageHandler?.Invoke(message);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[FacebookChatHost] WebMessage hatası");
            }
        }

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                _scraper?.Dispose();

                if (_webView != null)
                {
                    _webView.CoreWebView2?.Stop();
                    _webView.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[FacebookChatHost] Dispose hatası");
            }

            _scraper = null;
            _webView = null;
        }
    }
}