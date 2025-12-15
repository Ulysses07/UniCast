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
    /// Instagram Live Chat için WebView2 Host kontrolü.
    /// 
    /// Çalışma Prensibi:
    /// ================
    /// 1. WebView2 ile Instagram'a giriş yapılır (okuyucu hesap)
    /// 2. Yayıncının live sayfasına navigate edilir: instagram.com/{username}/live/
    /// 3. DOM'dan yorumlar scrape edilir
    /// 4. MutationObserver ile yeni yorumlar yakalanır
    /// 
    /// Test Edilmiş Selector'lar:
    /// - Kullanıcı adı: span._ap3a._aaco._aacw._aacx._aad7
    /// - Yorum metni: span._ap3a._aaco._aacu._aacx._aad7._aadf
    /// 
    /// URL Formatı:
    /// - https://www.instagram.com/{broadcaster}/live/
    /// </summary>
    public class InstagramChatHost : UserControl, IDisposable
    {
        #region Constants

        /// <summary>
        /// Instagram için ayrı WebView2 profil klasörü.
        /// Facebook ile çakışmaması için ayrı tutulur.
        /// </summary>
        public static readonly string InstagramWebView2UserDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UniCast", "WebView2_Instagram");

        private const string InstagramLoginUrl = "https://www.instagram.com/accounts/login/";
        private const string InstagramBaseUrl = "https://www.instagram.com";

        #endregion

        #region Fields

        private WebView2? _webView;
        private InstagramLiveChatScraper? _scraper;
        private bool _isInitialized;
        private bool _disposed;
        private TaskCompletionSource<bool>? _loadedTcs;
        private TaskCompletionSource<bool>? _loginTcs;
        private Action<string>? _messageHandler;

        // Okuyucu hesap bilgileri
        private readonly string? _readerUsername;
        private readonly string? _readerPassword;

        #endregion

        #region Properties

        /// <summary>
        /// Chat scraper instance'ı.
        /// </summary>
        public InstagramLiveChatScraper? Scraper => _scraper;

        /// <summary>
        /// WebView2 yüklendi mi?
        /// </summary>
        public new bool IsInitialized => _isInitialized;

        /// <summary>
        /// Instagram'a giriş yapılmış mı?
        /// </summary>
        public bool IsLoggedIn { get; private set; }

        /// <summary>
        /// Okuyucu hesap kullanıcı adı.
        /// </summary>
        public string? ReaderUsername => _readerUsername;

        #endregion

        #region Constructor

        /// <summary>
        /// Varsayılan constructor.
        /// </summary>
        public InstagramChatHost() : this(null, null)
        {
        }

        /// <summary>
        /// Okuyucu hesap bilgileri ile constructor.
        /// </summary>
        /// <param name="readerUsername">Okuyucu hesap kullanıcı adı</param>
        /// <param name="readerPassword">Okuyucu hesap şifresi</param>
        public InstagramChatHost(string? readerUsername, string? readerPassword)
        {
            _readerUsername = readerUsername;
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
            Log.Debug("[InstagramChatHost] Control Loaded event fired");
            _loadedTcs?.TrySetResult(true);
        }

        private async Task WaitForLoadedAsync(int timeoutMs = 10000)
        {
            // Zaten yüklüyse hemen dön
            if (IsLoaded)
            {
                Log.Debug("[InstagramChatHost] Kontrol zaten yüklü");
                return;
            }

            // Visual tree'de mi kontrol et
            if (System.Windows.Media.VisualTreeHelper.GetParent(this) != null)
            {
                Log.Debug("[InstagramChatHost] Kontrol visual tree'de - devam ediliyor");
                await Task.Delay(100);
                return;
            }

            _loadedTcs = new TaskCompletionSource<bool>();

            using var cts = new CancellationTokenSource(timeoutMs);
            cts.Token.Register(() => _loadedTcs.TrySetResult(false));

            var result = await _loadedTcs.Task;

            if (!result)
            {
                Log.Warning("[InstagramChatHost] Loaded event timeout ({Timeout}ms) - devam ediliyor", timeoutMs);
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
                Log.Debug("[InstagramChatHost] WebView2 başlatılıyor...");

                await WaitForLoadedAsync();

                _webView = new WebView2
                {
                    Width = 1,
                    Height = 1,
                    Visibility = Visibility.Hidden
                };

                Content = _webView;

                // Instagram için ayrı profil klasörü
                Log.Debug("[InstagramChatHost] UserDataFolder: {Folder}", InstagramWebView2UserDataFolder);
                Directory.CreateDirectory(InstagramWebView2UserDataFolder);

                // WebView2 Runtime kontrolü
                string? webView2Version = null;
                try
                {
                    webView2Version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                    Log.Debug("[InstagramChatHost] WebView2 Runtime version: {Version}", webView2Version);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[InstagramChatHost] WebView2 Runtime BULUNAMADI!");
                    throw new InvalidOperationException(
                        "WebView2 Runtime yüklü değil. Lütfen Microsoft Edge WebView2 Runtime'ı yükleyin.", ex);
                }

                var env = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: InstagramWebView2UserDataFolder);

                var initTask = _webView.EnsureCoreWebView2Async(env);

                using var timeoutCts = new CancellationTokenSource();
                var timeoutTask = Task.Delay(30000, timeoutCts.Token);
                var completedTask = await Task.WhenAny(initTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    Log.Error("[InstagramChatHost] EnsureCoreWebView2Async TIMEOUT (30 saniye)!");
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

                // User-Agent ayarla (mobil gibi davranmayı önle)
                _webView.CoreWebView2.Settings.UserAgent =
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

                // Navigation event'ini dinle
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                _isInitialized = true;
                Log.Information("[InstagramChatHost] WebView2 hazır (version: {Version})", webView2Version);

                // Login durumunu kontrol et
                await CheckLoginStatusAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[InstagramChatHost] WebView2 başlatma hatası");
                throw;
            }
        }

        #endregion

        #region Login Management

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                var url = _webView?.CoreWebView2?.Source ?? "";
                Log.Debug("[InstagramChatHost] Navigation completed: {Url}, Success: {Success}", url, e.IsSuccess);

                // Login sayfası değilse ve Instagram ana sayfası/feed ise login başarılı
                if (!url.Contains("/accounts/login") &&
                    !url.Contains("/challenge") &&
                    (url == InstagramBaseUrl || url == InstagramBaseUrl + "/" ||
                     url.Contains("instagram.com/") && !url.Contains("login")))
                {
                    if (!IsLoggedIn)
                    {
                        IsLoggedIn = true;
                        Log.Information("[InstagramChatHost] Login başarılı (URL: {Url})", url);
                        _loginTcs?.TrySetResult(true);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[InstagramChatHost] OnNavigationCompleted hatası");
            }
        }

        /// <summary>
        /// Instagram login durumunu kontrol eder.
        /// Cookie varlığını ve session geçerliliğini kontrol eder.
        /// </summary>
        public async Task<bool> CheckLoginStatusAsync()
        {
            if (_webView?.CoreWebView2 == null)
                return false;

            try
            {
                // Instagram ana sayfasına git ve login durumunu kontrol et
                var tcs = new TaskCompletionSource<bool>();

                void OnNavComplete(object? s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    _webView.CoreWebView2.NavigationCompleted -= OnNavComplete;
                    tcs.TrySetResult(e.IsSuccess);
                }

                _webView.CoreWebView2.NavigationCompleted += OnNavComplete;
                _webView.CoreWebView2.Navigate(InstagramBaseUrl);

                await Task.WhenAny(tcs.Task, Task.Delay(15000));

                // Sayfanın tam yüklenmesi için bekle
                await Task.Delay(3000);

                // Mevcut URL'i kontrol et
                var currentUrl = _webView.CoreWebView2.Source ?? "";

                // Login sayfasına yönlendirildik mi?
                if (currentUrl.Contains("/accounts/login") || currentUrl.Contains("/challenge"))
                {
                    IsLoggedIn = false;
                    Log.Debug("[InstagramChatHost] Login durumu: Giriş yapılmamış (URL: {Url})", currentUrl);
                    return false;
                }

                // JavaScript ile session_id cookie kontrolü yap
                var cookieCheck = await _webView.CoreWebView2.ExecuteScriptAsync(
                    "document.cookie.includes('sessionid') || document.cookie.includes('ds_user_id')");

                var hasCookie = cookieCheck?.Trim('"').ToLowerInvariant() == "true";

                // Ayrıca DOM'da login butonunun olup olmadığını kontrol et
                var hasLoginButton = await _webView.CoreWebView2.ExecuteScriptAsync(
                    "!!document.querySelector('a[href=\"/accounts/login/\"]') || !!document.querySelector('button[type=\"submit\"]')");
                var showsLoginButton = hasLoginButton?.Trim('"').ToLowerInvariant() == "true";

                IsLoggedIn = hasCookie && !showsLoginButton;

                Log.Debug("[InstagramChatHost] Login durumu: {Status} (URL: {Url}, Cookie: {Cookie}, LoginBtn: {Btn})",
                    IsLoggedIn ? "Giriş yapılmış" : "Giriş yapılmamış",
                    currentUrl, hasCookie, showsLoginButton);

                return IsLoggedIn;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[InstagramChatHost] Login durumu kontrol hatası");
                return false;
            }
        }

        /// <summary>
        /// Instagram'a giriş yapar.
        /// </summary>
        public async Task<bool> LoginAsync(string username, string password)
        {
            if (_webView?.CoreWebView2 == null)
            {
                await InitializeAsync();
            }

            if (_webView?.CoreWebView2 == null)
                return false;

            try
            {
                Log.Information("[InstagramChatHost] Instagram login başlatılıyor: @{Username}", username);

                _loginTcs = new TaskCompletionSource<bool>();

                // Login sayfasına git
                _webView.CoreWebView2.Navigate(InstagramLoginUrl);
                await Task.Delay(3000);

                // Form doldur ve gönder
                var loginScript = $@"
                    (function() {{
                        var usernameInput = document.querySelector('input[name=""username""]');
                        var passwordInput = document.querySelector('input[name=""password""]');
                        
                        if (!usernameInput || !passwordInput) {{
                            console.log('Login form bulunamadı');
                            return false;
                        }}
                        
                        // Username doldur
                        usernameInput.focus();
                        usernameInput.value = '{username.Replace("'", "\\'")}';
                        usernameInput.dispatchEvent(new Event('input', {{ bubbles: true }}));
                        
                        // Password doldur
                        passwordInput.focus();
                        passwordInput.value = '{password.Replace("'", "\\'")}';
                        passwordInput.dispatchEvent(new Event('input', {{ bubbles: true }}));
                        
                        // Submit butonunu bul ve tıkla
                        setTimeout(function() {{
                            var submitBtn = document.querySelector('button[type=""submit""]');
                            if (submitBtn) {{
                                submitBtn.click();
                                console.log('Login form gönderildi');
                            }}
                        }}, 500);
                        
                        return true;
                    }})();
                ";

                await _webView.CoreWebView2.ExecuteScriptAsync(loginScript);

                // Login sonucunu bekle (max 30 saniye)
                using var cts = new CancellationTokenSource(30000);
                cts.Token.Register(() => _loginTcs.TrySetResult(false));

                var result = await _loginTcs.Task;

                if (result)
                {
                    Log.Information("[InstagramChatHost] Login başarılı: @{Username}", username);
                }
                else
                {
                    // Tekrar kontrol et
                    await Task.Delay(2000);
                    var currentUrl = _webView.CoreWebView2.Source ?? "";
                    IsLoggedIn = !currentUrl.Contains("/accounts/login") && !currentUrl.Contains("/challenge");
                    result = IsLoggedIn;

                    if (result)
                    {
                        Log.Information("[InstagramChatHost] Login başarılı (gecikme): @{Username}", username);
                    }
                    else
                    {
                        Log.Warning("[InstagramChatHost] Login başarısız: @{Username}", username);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[InstagramChatHost] Login hatası");
                return false;
            }
        }

        #endregion

        #region Chat Operations

        /// <summary>
        /// Instagram Live Chat'i başlatır.
        /// </summary>
        /// <param name="broadcasterUsername">Yayıncının kullanıcı adı (@ olmadan)</param>
        /// <returns>Chat scraper instance'ı</returns>
        public async Task<InstagramLiveChatScraper> StartChatAsync(string broadcasterUsername, string? broadcastId = null)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(InstagramChatHost));

            if (string.IsNullOrWhiteSpace(broadcasterUsername))
                throw new ArgumentException("Yayıncı kullanıcı adı gerekli", nameof(broadcasterUsername));

            // @ işaretini temizle
            broadcasterUsername = broadcasterUsername.TrimStart('@').ToLowerInvariant();

            Log.Information("[InstagramChatHost] StartChatAsync - Yayıncı: @{Username}, BroadcastId: {BroadcastId}",
                broadcasterUsername, broadcastId ?? "N/A");

            if (!_isInitialized)
                await InitializeAsync();

            // Login kontrolü
            var isLoggedIn = await CheckLoginStatusAsync();
            if (!isLoggedIn)
            {
                // Okuyucu hesap bilgileri varsa otomatik login yap
                if (!string.IsNullOrWhiteSpace(_readerUsername) && !string.IsNullOrWhiteSpace(_readerPassword))
                {
                    Log.Information("[InstagramChatHost] Okuyucu hesap ile otomatik login yapılıyor: @{Username}", _readerUsername);
                    isLoggedIn = await LoginAsync(_readerUsername, _readerPassword);

                    if (!isLoggedIn)
                    {
                        throw new InvalidOperationException(
                            "Instagram okuyucu hesabına giriş yapılamadı. Lütfen Ayarlar > Instagram bölümünden hesap bilgilerini kontrol edin.");
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        "Instagram'a giriş yapılmamış. Lütfen önce Ayarlar > Instagram Ayarları'ndan okuyucu hesap bilgilerini girin.");
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

            // Yeni scraper oluştur - broadcast_id varsa URL'i oluştur
            string? liveUrl = null;
            if (!string.IsNullOrWhiteSpace(broadcastId))
            {
                liveUrl = $"https://www.instagram.com/{broadcasterUsername}/live/?broadcast_id={broadcastId}";
                Log.Debug("[InstagramChatHost] Live URL oluşturuldu: {Url}", liveUrl);
            }

            _scraper = new InstagramLiveChatScraper(broadcasterUsername, liveUrl);

            // WebView2 kontrollerini ayarla
            _scraper.SetWebViewControls(
                EnsureWebViewReadyAsync,
                NavigateAsync,
                ExecuteScriptAsync,
                RegisterMessageHandler,
                UnregisterMessageHandler
            );

            // Chat'i başlat
            Log.Debug("[InstagramChatHost] Scraper başlatılıyor...");
            await _scraper.StartAsync();

            Log.Information("[InstagramChatHost] Chat başlatıldı - @{Broadcaster}", broadcasterUsername);
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
                Log.Debug("[InstagramChatHost] Chat durduruluyor...");
                try
                {
                    await scraper.StopAsync();
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[InstagramChatHost] StopAsync hatası");
                }

                try
                {
                    scraper.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[InstagramChatHost] Scraper dispose hatası");
                }

                Log.Information("[InstagramChatHost] Chat durduruldu");
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
                Log.Warning("[InstagramChatHost] NavigateAsync - CoreWebView2 null");
                return;
            }

            Log.Debug("[InstagramChatHost] Navigating to: {Url}", url);

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
                Log.Warning("[InstagramChatHost] Navigation timeout (30s)");
                _webView.CoreWebView2.NavigationCompleted -= OnNavCompleted;
                tcs.TrySetResult(false);
            }

            await tcs.Task;
        }

        private async Task<string> ExecuteScriptAsync(string script)
        {
            if (_webView?.CoreWebView2 == null)
            {
                Log.Warning("[InstagramChatHost] ExecuteScriptAsync - CoreWebView2 null");
                return "";
            }

            try
            {
                return await _webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[InstagramChatHost] Script execution hatası");
                return "";
            }
        }

        private void RegisterMessageHandler(Action<string> handler)
        {
            if (_webView?.CoreWebView2 == null)
            {
                Log.Warning("[InstagramChatHost] RegisterMessageHandler - CoreWebView2 null");
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
                Log.Debug(ex, "[InstagramChatHost] WebMessage hatası");
            }
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Log.Debug("[InstagramChatHost] Dispose başlıyor...");

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
                Log.Debug(ex, "[InstagramChatHost] Scraper dispose hatası");
            }

            try
            {
                if (webView != null)
                {
                    if (webView.CoreWebView2 != null)
                    {
                        webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                        webView.CoreWebView2.Stop();
                    }
                    webView.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[InstagramChatHost] WebView dispose hatası");
            }

            Log.Debug("[InstagramChatHost] Dispose tamamlandı");
        }

        #endregion
    }
}