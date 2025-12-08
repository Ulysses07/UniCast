using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using UniCast.Core.Chat.Ingestors;
using UserControl = System.Windows.Controls.UserControl;

namespace UniCast.App.Views
{
    /// <summary>
    /// Facebook Live Chat için WebView2 Host kontrolü.
    /// Arka planda çalışır ve chat yorumlarını scrape eder.
    /// 
    /// DÜZELTME: WebView2 Collapsed durumda başlatılamaz!
    /// - Visibility.Hidden kullanılmalı (Collapsed değil)
    /// - Minimum 1x1 boyut gerekli
    /// - Loaded event beklenmeli
    /// </summary>
    public class FacebookChatHost : UserControl, IDisposable
    {
        private WebView2? _webView;
        private FacebookChatScraper? _scraper;
        private bool _isInitialized;
        private bool _disposed;
        private TaskCompletionSource<bool>? _loadedTcs;

        private Action<string>? _messageHandler;

        /// <summary>
        /// Chat scraper instance'ı.
        /// </summary>
        public FacebookChatScraper? Scraper => _scraper;

        /// <summary>
        /// WebView2 yüklendi mi?
        /// </summary>
        public new bool IsInitialized => _isInitialized;

        /// <summary>
        /// Facebook cookie'leri (login'den alınır).
        /// </summary>
        public string? Cookies { get; set; }

        public FacebookChatHost()
        {
            // DÜZELTME: Hidden kullan (Collapsed değil!) 
            // Collapsed, kontrolü visual tree'den tamamen çıkarır ve WebView2 başlatılamaz
            Visibility = Visibility.Hidden;

            // DÜZELTME: Minimum boyut gerekli (0x0 WebView2'yi kırar)
            Width = 800;
            Height = 600;

            // Opacity 0 yaparak tamamen görünmez yap
            this.Opacity = 100;

            // Loaded event'ini dinle
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Log.Debug("[FacebookChatHost] Control Loaded event fired");
            _loadedTcs?.TrySetResult(true);
        }

        /// <summary>
        /// Kontrolün yüklenmesini bekler
        /// </summary>
        private async Task WaitForLoadedAsync(int timeoutMs = 5000)
        {
            if (IsLoaded)
            {
                Log.Debug("[FacebookChatHost] Kontrol zaten yüklü");
                return;
            }

            _loadedTcs = new TaskCompletionSource<bool>();

            using var cts = new CancellationTokenSource(timeoutMs);
            cts.Token.Register(() => _loadedTcs.TrySetResult(false));

            var result = await _loadedTcs.Task;

            if (!result)
            {
                Log.Warning("[FacebookChatHost] Loaded event timeout ({Timeout}ms)", timeoutMs);
            }
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

                // Önce kontrol yüklenene kadar bekle
                await WaitForLoadedAsync();

                _webView = new WebView2
                {
                    // DÜZELTME: WebView2'nin kendisi de minimum boyutta olmalı
                    Width = 1,
                    Height = 1,
                    Visibility = Visibility.Hidden
                };
                Log.Debug("[FacebookChatHost] WebView2 kontrolü oluşturuldu");

                Content = _webView;

                // WebView2 ortamını oluştur
                var userDataFolder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UniCast", "WebView2_Chat");
                Log.Debug("[FacebookChatHost] UserDataFolder: {Folder}", userDataFolder);

                System.IO.Directory.CreateDirectory(userDataFolder);

                // WebView2 Runtime kontrolü
                string? webView2Version = null;
                try
                {
                    webView2Version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                    Log.Debug("[FacebookChatHost] WebView2 Runtime version: {Version}", webView2Version);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[FacebookChatHost] WebView2 Runtime BULUNAMADI!");
                    throw new InvalidOperationException("WebView2 Runtime yüklü değil. Lütfen Microsoft Edge WebView2 Runtime'ı yükleyin: https://developer.microsoft.com/en-us/microsoft-edge/webview2/", ex);
                }

                Log.Debug("[FacebookChatHost] CoreWebView2Environment oluşturuluyor...");
                var env = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: userDataFolder);

                Log.Debug("[FacebookChatHost] EnsureCoreWebView2Async çağrılıyor...");

                // DÜZELTME: Timeout ile çağır
                var initTask = _webView.EnsureCoreWebView2Async(env);

                // Timeout kontrolü - Task.WhenAny kullan
                using var timeoutCts = new CancellationTokenSource();
                var timeoutTask = Task.Delay(30000, timeoutCts.Token);
                var completedTask = await Task.WhenAny(initTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    Log.Error("[FacebookChatHost] EnsureCoreWebView2Async TIMEOUT (30 saniye)!");
                    throw new TimeoutException("WebView2 başlatma zaman aşımına uğradı");
                }

                // Timeout'u iptal et
                timeoutCts.Cancel();

                // initTask tamamlandıysa sonucu al (exception varsa fırlatılır)
                await initTask;

                Log.Debug("[FacebookChatHost] CoreWebView2 hazır, ayarlar yapılıyor...");

                // Ayarlar
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = true; // Debug için açık
                _webView.CoreWebView2.Settings.IsWebMessageEnabled = true;

                _isInitialized = true;
                Log.Information("[FacebookChatHost] WebView2 hazır (version: {Version})", webView2Version);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FacebookChatHost] WebView2 başlatma hatası: {Message}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Facebook Live chat'i başlatır.
        /// </summary>
        public async Task<FacebookChatScraper> StartChatAsync(string liveVideoUrl, string? cookies = null)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FacebookChatHost));

            Log.Debug("[FacebookChatHost] StartChatAsync başlıyor - URL: {Url}", liveVideoUrl);

            try
            {
                if (!_isInitialized)
                {
                    Log.Debug("[FacebookChatHost] Henüz initialize edilmemiş, InitializeAsync çağrılıyor...");
                    await InitializeAsync();
                }

                if (_webView?.CoreWebView2 == null)
                {
                    Log.Error("[FacebookChatHost] WebView2 veya CoreWebView2 null!");
                    throw new InvalidOperationException("WebView2 başlatılamadı");
                }

                // Cookie'leri ayarla
                var effectiveCookies = cookies ?? Cookies;
                if (!string.IsNullOrEmpty(effectiveCookies))
                {
                    Log.Debug("[FacebookChatHost] Cookie'ler ayarlanıyor...");
                    SetCookies(effectiveCookies);
                }

                // Mevcut scraper'ı durdur
                if (_scraper != null)
                {
                    Log.Debug("[FacebookChatHost] Mevcut scraper durduruluyor...");
                    await _scraper.StopAsync();
                    _scraper.Dispose();
                }

                // Yeni scraper oluştur
                Log.Debug("[FacebookChatHost] Yeni FacebookChatScraper oluşturuluyor...");
                _scraper = new FacebookChatScraper(liveVideoUrl);
                _scraper.Cookies = effectiveCookies;

                // WebView2 kontrollerini ayarla
                Log.Debug("[FacebookChatHost] SetWebViewControls çağrılıyor...");
                _scraper.SetWebViewControls(
                    ensureReady: EnsureWebViewReadyAsync,
                    navigate: NavigateAsync,
                    executeScript: ExecuteScriptAsync,
                    registerHandler: RegisterMessageHandler,
                    unregisterHandler: UnregisterMessageHandler
                );

                // Başlat
                Log.Debug("[FacebookChatHost] Scraper başlatılıyor...");
                await _scraper.StartAsync();

                Log.Information("[FacebookChatHost] Chat başarıyla başlatıldı");
                return _scraper;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FacebookChatHost] StartChatAsync hatası: {Message}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Chat'i durdurur.
        /// </summary>
        public async Task StopChatAsync()
        {
            // Thread-safe: local variable'a al
            var scraper = _scraper;
            _scraper = null;

            if (scraper != null)
            {
                Log.Debug("[FacebookChatHost] Chat durduruluyor...");
                try
                {
                    await scraper.StopAsync();
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[FacebookChatHost] StopAsync hatası (ignorable)");
                }

                try
                {
                    scraper.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[FacebookChatHost] Scraper dispose hatası (ignorable)");
                }

                Log.Debug("[FacebookChatHost] Chat durduruldu");
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
                int count = 0;

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
                        count++;
                    }
                }

                Log.Debug("[FacebookChatHost] {Count} cookie ayarlandı", count);
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
            {
                Log.Warning("[FacebookChatHost] NavigateAsync - CoreWebView2 null");
                return;
            }

            Log.Debug("[FacebookChatHost] Navigating to: {Url}", url);

            var tcs = new TaskCompletionSource<bool>();

            void OnNavigationCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;

                if (e.IsSuccess)
                {
                    Log.Debug("[FacebookChatHost] Navigation başarılı");
                }
                else
                {
                    Log.Warning("[FacebookChatHost] Navigation başarısız: {Status}", e.WebErrorStatus);
                }

                tcs.TrySetResult(e.IsSuccess);
            }

            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.Navigate(url);

            // Timeout ile bekle
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(30000));
            if (completedTask != tcs.Task)
            {
                Log.Warning("[FacebookChatHost] Navigation timeout (30s)");
                _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                tcs.TrySetResult(false);
            }

            await tcs.Task;
        }

        private async Task<string> ExecuteScriptAsync(string script)
        {
            if (_webView?.CoreWebView2 == null)
            {
                Log.Warning("[FacebookChatHost] ExecuteScriptAsync - CoreWebView2 null");
                return "";
            }

            try
            {
                return await _webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[FacebookChatHost] Script execution hatası");
                return "";
            }
        }

        private void RegisterMessageHandler(Action<string> handler)
        {
            if (_webView?.CoreWebView2 == null)
            {
                Log.Warning("[FacebookChatHost] RegisterMessageHandler - CoreWebView2 null");
                return;
            }

            _messageHandler = handler;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            Log.Debug("[FacebookChatHost] Message handler registered");
        }

        private void UnregisterMessageHandler()
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            }
            _messageHandler = null;
            Log.Debug("[FacebookChatHost] Message handler unregistered");
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var message = e.WebMessageAsJson;
                Log.Debug("[FacebookChatHost] WebMessage alındı: {Length} chars", message?.Length ?? 0);

                if (!string.IsNullOrEmpty(message))
                {
                    _messageHandler?.Invoke(message);
                }
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
            Log.Debug("[FacebookChatHost] Dispose başlıyor...");

            try
            {
                Loaded -= OnLoaded;
                _loadedTcs?.TrySetResult(false);
            }
            catch { }

            // Thread-safe: local variable'lara al
            var scraper = _scraper;
            var webView = _webView;
            _scraper = null;
            _webView = null;

            try
            {
                scraper?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[FacebookChatHost] Scraper dispose hatası");
            }

            try
            {
                if (webView != null)
                {
                    webView.CoreWebView2?.Stop();
                    webView.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[FacebookChatHost] WebView dispose hatası");
            }

            Log.Debug("[FacebookChatHost] Dispose tamamlandı");
        }
    }
}