using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Serilog;
using UniCast.Core.Http;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace UniCast.App.Views
{
    /// <summary>
    /// Facebook OAuth penceresi.
    /// Graph API için Page Access Token alır.
    /// </summary>
    public partial class FacebookOAuthWindow : Window
    {
        // Facebook App bilgileri
        private const string AppId = "1593694605379011";
        private const string RedirectUri = "https://localhost/callback";
        private const string Scope = "pages_read_engagement,pages_read_user_content";

        // OAuth URL
        private static readonly string OAuthUrl =
            $"https://www.facebook.com/v18.0/dialog/oauth?" +
            $"client_id={AppId}&" +
            $"redirect_uri={Uri.EscapeDataString(RedirectUri)}&" +
            $"scope={Scope}&" +
            $"response_type=token";

        /// <summary>
        /// Alınan User Access Token.
        /// </summary>
        public string? UserAccessToken { get; private set; }

        /// <summary>
        /// Long-lived User Access Token (60 gün geçerli).
        /// </summary>
        public string? LongLivedToken { get; private set; }

        /// <summary>
        /// Seçilen sayfa bilgileri.
        /// </summary>
        public FacebookPageInfo? SelectedPage { get; private set; }

        /// <summary>
        /// Token son kullanma tarihi.
        /// </summary>
        public DateTime? TokenExpiry { get; private set; }

        private bool _tokenReceived;

        public FacebookOAuthWindow()
        {
            InitializeComponent();
            Loaded += FacebookOAuthWindow_Loaded;
        }

        private async void FacebookOAuthWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeWebViewAsync();
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                TxtStatus.Text = "WebView2 başlatılıyor...";

                // WebView2 ortamını oluştur
                var userDataFolder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UniCast", "WebView2", "FacebookOAuth");

                System.IO.Directory.CreateDirectory(userDataFolder);

                var env = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: userDataFolder);

                await webView.EnsureCoreWebView2Async(env);

                // WebView ayarları
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

                // Navigation olaylarını dinle
                webView.NavigationStarting += WebView_NavigationStarting;
                webView.NavigationCompleted += WebView_NavigationCompleted;

                TxtStatus.Text = "Facebook'a yönlendiriliyorsunuz...";

                // OAuth sayfasına git
                webView.Source = new Uri(OAuthUrl);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FacebookOAuth] WebView2 başlatma hatası");
                TxtStatus.Text = $"Hata: {ex.Message}";

                MessageBox.Show(
                    $"WebView2 başlatılamadı.\n\n{ex.Message}\n\n" +
                    "WebView2 Runtime yüklü olduğundan emin olun.",
                    "Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void WebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            Log.Debug("[FacebookOAuth] Navigating to: {Url}", e.Uri);

            // Callback URL'e yönlendirme mi?
            if (e.Uri.StartsWith(RedirectUri))
            {
                e.Cancel = true; // Navigasyonu durdur
                await ProcessCallbackAsync(e.Uri);
            }
        }

        private void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess && !_tokenReceived)
            {
                Log.Warning("[FacebookOAuth] Navigation failed: {Status}", e.WebErrorStatus);
                return;
            }

            // Loading overlay'i gizle
            LoadingOverlay.Visibility = Visibility.Collapsed;
            webView.Visibility = Visibility.Visible;

            TxtStatus.Text = "Sayfanızı seçin ve izinleri onaylayın.";
        }

        private async Task ProcessCallbackAsync(string callbackUrl)
        {
            try
            {
                Log.Information("[FacebookOAuth] Callback alındı");
                TxtStatus.Text = "Token alınıyor...";

                // URL'den token'ları parse et
                // Format: https://localhost/callback#access_token=...&long_lived_token=...
                var uri = new Uri(callbackUrl);
                var fragment = uri.Fragment.TrimStart('#');
                var queryParams = HttpUtility.ParseQueryString(fragment);

                var accessToken = queryParams["access_token"];
                var longLivedToken = queryParams["long_lived_token"];
                var expiresIn = queryParams["expires_in"];

                if (string.IsNullOrEmpty(accessToken))
                {
                    // Hata kontrolü
                    var error = queryParams["error"];
                    var errorDescription = queryParams["error_description"];

                    throw new Exception($"Token alınamadı: {error} - {errorDescription}");
                }

                UserAccessToken = accessToken;
                LongLivedToken = longLivedToken ?? accessToken;

                // Token süresi
                if (int.TryParse(expiresIn, out var seconds))
                {
                    TokenExpiry = DateTime.UtcNow.AddSeconds(seconds);
                }

                _tokenReceived = true;

                // Sayfaları al
                TxtStatus.Text = "Sayfalar yükleniyor...";
                var pages = await GetPagesAsync(LongLivedToken);

                if (pages == null || pages.Count == 0)
                {
                    throw new Exception(
                        "Erişilebilir Facebook sayfası bulunamadı.\n\n" +
                        "Lütfen şunları kontrol edin:\n" +
                        "• Bir Facebook Sayfanız var mı?\n" +
                        "• Sayfa en az 60 günlük mü?\n" +
                        "• Sayfa en az 100 takipçiye sahip mi?\n" +
                        "• OAuth sırasında sayfayı seçtiniz mi?");
                }

                // Sayfa seçimi (tek sayfa varsa otomatik seç)
                if (pages.Count == 1)
                {
                    SelectedPage = pages[0];
                }
                else
                {
                    // Birden fazla sayfa varsa kullanıcıya sor
                    SelectedPage = await ShowPageSelectionDialogAsync(pages);

                    if (SelectedPage == null)
                    {
                        DialogResult = false;
                        Close();
                        return;
                    }
                }

                Log.Information("[FacebookOAuth] Sayfa seçildi: {PageName} ({PageId})",
                    SelectedPage.Name, SelectedPage.Id);

                // Başarı
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        $"Facebook sayfanız başarıyla bağlandı!\n\n" +
                        $"Sayfa: {SelectedPage.Name}\n" +
                        $"ID: {SelectedPage.Id}\n\n" +
                        "Artık bu sayfanın canlı yayın yorumlarını görebilirsiniz.",
                        "Bağlantı Başarılı",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    DialogResult = true;
                    Close();
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FacebookOAuth] Callback işleme hatası");

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        $"Facebook bağlantısı başarısız:\n\n{ex.Message}",
                        "Hata",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);

                    DialogResult = false;
                    Close();
                });
            }
        }

        private async Task<List<FacebookPageInfo>> GetPagesAsync(string accessToken)
        {
            var url = $"https://graph.facebook.com/v18.0/me/accounts?access_token={accessToken}";

            try
            {
                using var response = await SharedHttpClients.GraphApi.GetAsync(url);
                var content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Log.Error("[FacebookOAuth] Pages API hatası: {Content}", content);
                    return new List<FacebookPageInfo>();
                }

                using var doc = JsonDocument.Parse(content);
                var data = doc.RootElement.GetProperty("data");

                var pages = new List<FacebookPageInfo>();
                foreach (var page in data.EnumerateArray())
                {
                    pages.Add(new FacebookPageInfo
                    {
                        Id = page.GetProperty("id").GetString() ?? "",
                        Name = page.GetProperty("name").GetString() ?? "",
                        AccessToken = page.GetProperty("access_token").GetString() ?? "",
                        Category = page.TryGetProperty("category", out var cat) ? cat.GetString() : null
                    });
                }

                return pages;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FacebookOAuth] Pages alma hatası");
                return new List<FacebookPageInfo>();
            }
        }

        private Task<FacebookPageInfo?> ShowPageSelectionDialogAsync(List<FacebookPageInfo> pages)
        {
            return Application.Current.Dispatcher.InvokeAsync<FacebookPageInfo?>(() =>
            {
                // Basit bir sayfa seçim dialogu
                var pageNames = new List<string>();
                foreach (var page in pages)
                {
                    pageNames.Add($"{page.Name} ({page.Category ?? "Sayfa"})");
                }

                var message = "Birden fazla sayfa bulundu. Hangisini kullanmak istersiniz?\n\n";
                for (int i = 0; i < pages.Count; i++)
                {
                    message += $"{i + 1}. {pageNames[i]}\n";
                }
                message += "\nSeçiminizi girin (1-" + pages.Count + "):";

                // InputBox yok, ilk sayfayı seç (veya custom dialog yapılabilir)
                // Şimdilik ilk sayfayı seçiyoruz
                return pages[0];
            }).Task;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                webView.NavigationStarting -= WebView_NavigationStarting;
                webView.NavigationCompleted -= WebView_NavigationCompleted;
                webView.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[FacebookOAuth] Cleanup hatası");
            }

            base.OnClosed(e);
        }
    }

    /// <summary>
    /// Facebook sayfa bilgileri.
    /// </summary>
    public class FacebookPageInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string? Category { get; set; }
    }
}