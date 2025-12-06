using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Serilog;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace UniCast.App.Views
{
    /// <summary>
    /// Facebook login penceresi.
    /// WebView2 kullanarak Facebook'a güvenli giriş yapar ve cookie'leri alır.
    /// </summary>
    public partial class FacebookLoginWindow : Window
    {
        private const string FacebookLoginUrl = "https://www.facebook.com/login";
        private const string FacebookBaseUrl = "https://www.facebook.com";
        
        /// <summary>
        /// Başarılı login sonrası alınan cookie'ler.
        /// </summary>
        public string? FacebookCookies { get; private set; }
        
        /// <summary>
        /// Kullanıcının c_user ID'si (Facebook User ID).
        /// </summary>
        public string? FacebookUserId { get; private set; }
        
        private bool _isInitialized;
        private bool _loginCompleted;
        
        public FacebookLoginWindow()
        {
            InitializeComponent();
            Loaded += FacebookLoginWindow_Loaded;
        }
        
        private async void FacebookLoginWindow_Loaded(object sender, RoutedEventArgs e)
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
                    "UniCast", "WebView2");
                
                System.IO.Directory.CreateDirectory(userDataFolder);
                
                var env = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: userDataFolder);
                
                await webView.EnsureCoreWebView2Async(env);
                
                // WebView ayarları
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                
                // Navigation olaylarını dinle
                webView.NavigationStarting += WebView_NavigationStarting;
                webView.NavigationCompleted += WebView_NavigationCompleted;
                
                _isInitialized = true;
                
                TxtStatus.Text = "Facebook yükleniyor...";
                
                // Facebook login sayfasına git
                webView.Source = new Uri(FacebookLoginUrl);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FacebookLogin] WebView2 başlatma hatası");
                TxtStatus.Text = $"Hata: {ex.Message}";
                
                MessageBox.Show(
                    $"WebView2 başlatılamadı.\n\n{ex.Message}\n\n" +
                    "WebView2 Runtime yüklü olduğundan emin olun:\n" +
                    "https://developer.microsoft.com/en-us/microsoft-edge/webview2/",
                    "Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        
        private void WebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            Log.Debug("[FacebookLogin] Navigating to: {Url}", e.Uri);
        }
        
        private async void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                Log.Warning("[FacebookLogin] Navigation failed: {Status}", e.WebErrorStatus);
                return;
            }
            
            var url = webView.Source?.ToString() ?? "";
            Log.Debug("[FacebookLogin] Navigation completed: {Url}", url);
            
            // Loading overlay'i gizle, WebView'ı göster
            LoadingOverlay.Visibility = Visibility.Collapsed;
            webView.Visibility = Visibility.Visible;
            
            // Login başarılı mı kontrol et
            if (await CheckLoginStatusAsync())
            {
                return; // Login tamamlandı, pencere kapanacak
            }
            
            // Login sayfasında mı kontrol et
            if (url.Contains("login") || url.Contains("checkpoint"))
            {
                TxtStatus.Text = "Lütfen Facebook hesabınıza giriş yapın.";
            }
        }
        
        private async Task<bool> CheckLoginStatusAsync()
        {
            if (_loginCompleted || !_isInitialized)
                return false;
            
            try
            {
                // Facebook cookie'lerini al
                var cookies = await webView.CoreWebView2.CookieManager
                    .GetCookiesAsync(FacebookBaseUrl);
                
                // c_user cookie'si varsa login başarılı
                var cUserCookie = cookies.FirstOrDefault(c => c.Name == "c_user");
                var xsCookie = cookies.FirstOrDefault(c => c.Name == "xs");
                
                if (cUserCookie != null && xsCookie != null)
                {
                    Log.Information("[FacebookLogin] Login başarılı! User ID: {UserId}", cUserCookie.Value);
                    
                    // Tüm gerekli cookie'leri topla
                    var essentialCookies = new[] { "c_user", "xs", "fr", "datr", "sb" };
                    var cookieList = new List<string>();
                    
                    foreach (var cookie in cookies)
                    {
                        if (essentialCookies.Contains(cookie.Name) || 
                            cookie.Name.StartsWith("ps_") ||
                            cookie.Name.StartsWith("presence"))
                        {
                            cookieList.Add($"{cookie.Name}={cookie.Value}");
                        }
                    }
                    
                    FacebookCookies = string.Join("; ", cookieList);
                    FacebookUserId = cUserCookie.Value;
                    
                    _loginCompleted = true;
                    
                    // Başarı mesajı göster
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        MessageBox.Show(
                            "Facebook hesabınıza başarıyla bağlandı!\n\n" +
                            "Artık Facebook canlı yayın chatinizi görebilirsiniz.",
                            "Bağlantı Başarılı",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        
                        DialogResult = true;
                        Close();
                    });
                    
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FacebookLogin] Cookie kontrol hatası");
            }
            
            return false;
        }
        
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
        
        protected override void OnClosed(EventArgs e)
        {
            // WebView'ı temizle
            try
            {
                webView.NavigationStarting -= WebView_NavigationStarting;
                webView.NavigationCompleted -= WebView_NavigationCompleted;
                webView.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[FacebookLogin] Cleanup hatası");
            }
            
            base.OnClosed(e);
        }
    }
}
