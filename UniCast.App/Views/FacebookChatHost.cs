using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;
using System;
using System.IO;
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
    /// 
    /// YENİ YAKLAŞIM: Okuyucu Hesap Sistemi
    /// =====================================
    /// Instagram'da olduğu gibi, ana hesap yerine ayrı bir "okuyucu hesap" kullanılır.
    /// Bu sayede:
    /// - Ana hesap korunur (checkpoint riski yok)
    /// - Tek WebView2 profili kullanılır (cookie transfer yok)
    /// - Facebook aynı cihazı görür (güvenlik sorunu yok)
    /// 
    /// SHARED WEBVIEW2 PROFİL
    /// ======================
    /// Tüm Facebook işlemleri (login, chat scraping) aynı profili kullanır.
    /// Profil yolu: %LOCALAPPDATA%\UniCast\WebView2
    /// </summary>
    public class FacebookChatHost : UserControl, IDisposable
    {
        #region Constants

        /// <summary>
        /// Paylaşılan WebView2 profil klasörü.
        /// TÜM Facebook işlemleri bu profili kullanır.
        /// </summary>
        public static readonly string SharedWebView2UserDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UniCast", "WebView2");

        private const string FacebookLoginUrl = "https://www.facebook.com/login";
        private const string FacebookBaseUrl = "https://www.facebook.com";

        #endregion

        #region Fields

        private WebView2? _webView;
        private FacebookChatScraper? _scraper;
        private bool _isInitialized;
        private bool _disposed;
        private TaskCompletionSource<bool>? _loadedTcs;
        private TaskCompletionSource<bool>? _loginTcs;
        private Action<string>? _messageHandler;

        // Okuyucu hesap bilgileri
        private readonly string? _readerEmail;
        private readonly string? _readerPassword;

        #endregion

        #region Properties

        /// <summary>
        /// Chat scraper instance'ı.
        /// </summary>
        public FacebookChatScraper? Scraper => _scraper;

        /// <summary>
        /// WebView2 yüklendi mi?
        /// </summary>
        public new bool IsInitialized => _isInitialized;

        /// <summary>
        /// Facebook'a giriş yapılmış mı?
        /// </summary>
        public bool IsLoggedIn { get; private set; }

        /// <summary>
        /// Okuyucu hesap e-postası.
        /// </summary>
        public string? ReaderEmail => _readerEmail;

        #endregion

        #region Constructor

        /// <summary>
        /// Varsayılan constructor.
        /// </summary>
        public FacebookChatHost() : this(null, null)
        {
        }

        /// <summary>
        /// Okuyucu hesap bilgileri ile constructor.
        /// </summary>
        /// <param name="readerEmail">Okuyucu hesap e-posta/telefon</param>
        /// <param name="readerPassword">Okuyucu hesap şifresi</param>
        public FacebookChatHost(string? readerEmail, string? readerPassword)
        {
            _readerEmail = readerEmail;
            _readerPassword = readerPassword;

            // Hidden kullan (Collapsed değil!) 
            // Collapsed, kontrolü visual tree'den tamamen çıkarır ve WebView2 başlatılamaz
            Visibility = Visibility.Hidden;

            // Minimum boyut gerekli (0x0 WebView2'yi kırar)
            Width = 1;
            Height = 1;

            // Loaded event'ini dinle
            Loaded += OnLoaded;
        }

        #endregion

        #region Initialization

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Log.Debug("[FacebookChatHost] Control Loaded event fired");
            _loadedTcs?.TrySetResult(true);
        }

        private async Task WaitForLoadedAsync(int timeoutMs = 10000)
        {
            // Zaten yüklüyse hemen dön
            if (IsLoaded)
            {
                Log.Debug("[FacebookChatHost] Kontrol zaten yüklü");
                return;
            }

            // Visual tree'de mi kontrol et
            if (System.Windows.Media.VisualTreeHelper.GetParent(this) != null)
            {
                Log.Debug("[FacebookChatHost] Kontrol visual tree'de - devam ediliyor");
                // Kısa bir bekleme yap UI'ın yerleşmesi için
                await Task.Delay(100);
                return;
            }

            _loadedTcs = new TaskCompletionSource<bool>();

            using var cts = new CancellationTokenSource(timeoutMs);
            cts.Token.Register(() => _loadedTcs.TrySetResult(false));

            var result = await _loadedTcs.Task;

            if (!result)
            {
                Log.Warning("[FacebookChatHost] Loaded event timeout ({Timeout}ms) - devam ediliyor", timeoutMs);
                // Timeout olsa bile devam et, belki çalışır
            }
        }

        /// <summary>
        /// WebView2'yi başlatır. Shared profil kullanır.
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized || _disposed)
                return;

            try
            {
                Log.Debug("[FacebookChatHost] WebView2 başlatılıyor (Shared profil)...");

                await WaitForLoadedAsync();

                _webView = new WebView2
                {
                    Width = 1,
                    Height = 1,
                    Visibility = Visibility.Hidden
                };

                Content = _webView;

                // SHARED PROFİL KULLAN
                Log.Debug("[FacebookChatHost] Shared UserDataFolder: {Folder}", SharedWebView2UserDataFolder);
                Directory.CreateDirectory(SharedWebView2UserDataFolder);

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
                    throw new InvalidOperationException(
                        "WebView2 Runtime yüklü değil. Lütfen Microsoft Edge WebView2 Runtime'ı yükleyin.", ex);
                }

                var env = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: SharedWebView2UserDataFolder);

                var initTask = _webView.EnsureCoreWebView2Async(env);

                using var timeoutCts = new CancellationTokenSource();
                var timeoutTask = Task.Delay(30000, timeoutCts.Token);
                var completedTask = await Task.WhenAny(initTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    Log.Error("[FacebookChatHost] EnsureCoreWebView2Async TIMEOUT (30 saniye)!");
                    throw new TimeoutException("WebView2 başlatma zaman aşımına uğradı");
                }

                timeoutCts.Cancel();
                await initTask;

                // Ayarlar
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _webView.CoreWebView2.Settings.IsWebMessageEnabled = true;

                // Navigation event'ini dinle (login kontrolü için)
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                _isInitialized = true;
                Log.Information("[FacebookChatHost] WebView2 hazır (Shared profil, version: {Version})", webView2Version);

                // Login durumunu kontrol et
                await CheckLoginStatusAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FacebookChatHost] WebView2 başlatma hatası");
                throw;
            }
        }

        #endregion

        #region Login Management

        /// <summary>
        /// Facebook'a giriş yapılmış mı kontrol eder.
        /// Cookie'lerde c_user ve xs olup olmadığına bakar.
        /// </summary>
        public async Task<bool> CheckLoginStatusAsync()
        {
            if (_webView?.CoreWebView2 == null)
            {
                Log.Warning("[FacebookChatHost] CheckLoginStatus - CoreWebView2 null");
                return false;
            }

            try
            {
                var cookieManager = _webView.CoreWebView2.CookieManager;
                var cookies = await cookieManager.GetCookiesAsync(FacebookBaseUrl);

                bool hasUserId = false;
                bool hasSession = false;

                foreach (var cookie in cookies)
                {
                    if (cookie.Name == "c_user" && !string.IsNullOrEmpty(cookie.Value))
                        hasUserId = true;
                    if (cookie.Name == "xs" && !string.IsNullOrEmpty(cookie.Value))
                        hasSession = true;
                }

                IsLoggedIn = hasUserId && hasSession;
                Log.Information("[FacebookChatHost] Login durumu: {Status} (c_user: {HasUser}, xs: {HasSession})",
                    IsLoggedIn ? "GİRİŞ YAPILMIŞ" : "GİRİŞ YAPILMAMIŞ", hasUserId, hasSession);

                return IsLoggedIn;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[FacebookChatHost] Login kontrolü hatası");
                return false;
            }
        }

        /// <summary>
        /// Facebook'a okuyucu hesap ile giriş yapar.
        /// </summary>
        /// <param name="email">E-posta veya telefon</param>
        /// <param name="password">Şifre</param>
        /// <returns>Başarılı mı?</returns>
        public async Task<bool> LoginAsync(string email, string password)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FacebookChatHost));

            if (!_isInitialized)
                await InitializeAsync();

            if (_webView?.CoreWebView2 == null)
                throw new InvalidOperationException("WebView2 başlatılamadı");

            Log.Information("[FacebookChatHost] Facebook login başlıyor...");

            try
            {
                _loginTcs = new TaskCompletionSource<bool>();

                // Login sayfasına git
                await NavigateAsync(FacebookLoginUrl);
                await Task.Delay(2000); // Sayfa yüklensin

                // Form doldur
                var fillScript = $@"
                    (function() {{
                        var emailField = document.getElementById('email') || document.querySelector('input[name=""email""]');
                        var passField = document.getElementById('pass') || document.querySelector('input[name=""pass""]');
                        var loginBtn = document.querySelector('button[name=""login""]') || document.querySelector('button[type=""submit""]');
                        
                        if (emailField && passField) {{
                            emailField.value = '{EscapeJsString(email)}';
                            passField.value = '{EscapeJsString(password)}';
                            
                            // Input event'lerini tetikle (React için)
                            emailField.dispatchEvent(new Event('input', {{ bubbles: true }}));
                            passField.dispatchEvent(new Event('input', {{ bubbles: true }}));
                            
                            return 'fields_filled';
                        }}
                        return 'fields_not_found';
                    }})();
                ";

                var fillResult = await ExecuteScriptAsync(fillScript);
                Log.Debug("[FacebookChatHost] Form doldurma sonucu: {Result}", fillResult);

                if (fillResult.Contains("fields_not_found"))
                {
                    Log.Warning("[FacebookChatHost] Login form alanları bulunamadı");
                    return false;
                }

                await Task.Delay(500);

                // Login butonuna tıkla
                var clickScript = @"
                    (function() {
                        var loginBtn = document.querySelector('button[name=""login""]') || 
                                       document.querySelector('button[type=""submit""]') ||
                                       document.querySelector('button[data-testid=""royal_login_button""]');
                        if (loginBtn) {
                            loginBtn.click();
                            return 'clicked';
                        }
                        return 'button_not_found';
                    })();
                ";

                var clickResult = await ExecuteScriptAsync(clickScript);
                Log.Debug("[FacebookChatHost] Login butonu tıklama: {Result}", clickResult);

                // Login sonucunu bekle (max 30 saniye)
                var waitTask = _loginTcs.Task;
                var timeoutTask = Task.Delay(30000);

                var completedTask = await Task.WhenAny(waitTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    Log.Warning("[FacebookChatHost] Login timeout (30s)");
                    // Timeout olsa bile login durumunu kontrol et
                    return await CheckLoginStatusAsync();
                }

                return await waitTask;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FacebookChatHost] Login hatası");
                return false;
            }
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_webView?.CoreWebView2 == null) return;

            var url = _webView.CoreWebView2.Source;
            Log.Debug("[FacebookChatHost] Navigation tamamlandı: {Url}", url);

            // Checkpoint kontrolü
            if (url.Contains("checkpoint"))
            {
                Log.Warning("[FacebookChatHost] ⚠️ Facebook güvenlik kontrolü algılandı!");
                Log.Warning("[FacebookChatHost] Okuyucu hesabınız için doğrulama gerekiyor.");
                Log.Warning("[FacebookChatHost] Tarayıcıda manuel olarak doğrulama yapın veya yeni okuyucu hesap oluşturun.");
                _loginTcs?.TrySetResult(false);
                return;
            }

            // Login başarılı mı kontrol et
            if (_loginTcs != null && !_loginTcs.Task.IsCompleted)
            {
                // Ana sayfa veya feed'e yönlendirildiyse başarılı
                if (url == FacebookBaseUrl ||
                    url == FacebookBaseUrl + "/" ||
                    url.Contains("facebook.com/?") ||
                    url.Contains("facebook.com/home"))
                {
                    Task.Run(async () =>
                    {
                        var isLoggedIn = await CheckLoginStatusAsync();
                        _loginTcs?.TrySetResult(isLoggedIn);
                    });
                }
            }
        }

        /// <summary>
        /// Facebook'tan çıkış yapar.
        /// </summary>
        public async Task LogoutAsync()
        {
            if (_webView?.CoreWebView2 == null)
                return;

            try
            {
                // Cookie'leri temizle
                var cookieManager = _webView.CoreWebView2.CookieManager;
                cookieManager.DeleteAllCookies();

                IsLoggedIn = false;
                Log.Information("[FacebookChatHost] Facebook'tan çıkış yapıldı");

                await Task.CompletedTask; // Async signature için
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[FacebookChatHost] Logout hatası");
            }
        }

        private string EscapeJsString(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
        }

        #endregion

        #region Chat Scraping

        /// <summary>
        /// Facebook Live chat'i başlatır.
        /// Önce login durumunu kontrol eder.
        /// </summary>
        public async Task<FacebookChatScraper> StartChatAsync(string liveVideoUrl)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FacebookChatHost));

            Log.Information("[FacebookChatHost] StartChatAsync - URL: {Url}", liveVideoUrl);

            if (!_isInitialized)
                await InitializeAsync();

            // Login kontrolü
            var isLoggedIn = await CheckLoginStatusAsync();
            if (!isLoggedIn)
            {
                // Okuyucu hesap bilgileri varsa otomatik login yap
                if (!string.IsNullOrWhiteSpace(_readerEmail) && !string.IsNullOrWhiteSpace(_readerPassword))
                {
                    Log.Information("[FacebookChatHost] Okuyucu hesap ile otomatik login yapılıyor...");
                    isLoggedIn = await LoginAsync(_readerEmail, _readerPassword);

                    if (!isLoggedIn)
                    {
                        throw new InvalidOperationException(
                            "Facebook okuyucu hesabına giriş yapılamadı. Lütfen Ayarlar > Facebook bölümünden hesap bilgilerini kontrol edin.");
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        "Facebook'a giriş yapılmamış. Lütfen önce Ayarlar > Facebook Ayarları'ndan okuyucu hesap ile bağlanın.");
                }
            }

            if (_webView?.CoreWebView2 == null)
                throw new InvalidOperationException("WebView2 başlatılamadı");

            // Mevcut scraper'ı durdur
            if (_scraper != null)
            {
                await _scraper.StopAsync();
                _scraper.Dispose();
            }

            // Yeni scraper oluştur
            _scraper = new FacebookChatScraper(liveVideoUrl);

            // WebView2 kontrollerini ayarla
            _scraper.SetWebViewControls(
                EnsureWebViewReadyAsync,
                NavigateAsync,
                ExecuteScriptAsync,
                RegisterMessageHandler,
                UnregisterMessageHandler
            );

            // Chat'i başlat
            Log.Debug("[FacebookChatHost] Scraper başlatılıyor...");
            await _scraper.StartAsync();

            Log.Information("[FacebookChatHost] Chat başlatıldı");
            return _scraper;
        }

        /// <summary>
        /// Chat'i durdurur.
        /// </summary>
        public async Task StopChatAsync()
        {
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
                    Log.Debug(ex, "[FacebookChatHost] StopAsync hatası");
                }

                try
                {
                    scraper.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[FacebookChatHost] Scraper dispose hatası");
                }

                Log.Information("[FacebookChatHost] Chat durduruldu");
            }
        }

        #endregion

        #region WebView2 Controls

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

            void OnNavCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                _webView.CoreWebView2.NavigationCompleted -= OnNavCompleted;
                tcs.TrySetResult(e.IsSuccess);
            }

            _webView.CoreWebView2.NavigationCompleted += OnNavCompleted;
            _webView.CoreWebView2.Navigate(url);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(30000));
            if (completedTask != tcs.Task)
            {
                Log.Warning("[FacebookChatHost] Navigation timeout (30s)");
                _webView.CoreWebView2.NavigationCompleted -= OnNavCompleted;
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

        #region Dispose

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
                _loginTcs?.TrySetResult(false);
            }
            catch { }

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
                    webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
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

        #endregion
    }
}