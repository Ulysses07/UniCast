using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Serilog;
using UniCast.App.ViewModels;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using Panel = System.Windows.Controls.Panel;

namespace UniCast.App.Views
{
    public partial class SettingsView : System.Windows.Controls.UserControl
    {
        // Facebook login için kullanılacak (lazy initialization)
        private FacebookChatHost? _facebookChatHost;

        public SettingsView()
        {
            InitializeComponent();
            this.Loaded += SettingsView_Loaded;
            this.Unloaded += SettingsView_Unloaded;
        }

        private void SettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is SettingsViewModel vm)
            {
                // Twitch OAuth
                var twitchPwd = this.FindName("PwdTwitchOAuth") as PasswordBox;
                if (twitchPwd != null && twitchPwd.Password != vm.TwitchOAuthToken)
                    twitchPwd.Password = vm.TwitchOAuthToken ?? "";

                // Instagram
                var igPwd = this.FindName("PwdInstagramSession") as PasswordBox;
                if (igPwd != null && igPwd.Password != vm.InstagramSessionId)
                    igPwd.Password = vm.InstagramSessionId ?? "";

                // Facebook Reader Password
                var fbPwd = this.FindName("PwdFacebookReaderPassword") as PasswordBox;
                if (fbPwd != null && fbPwd.Password != vm.FacebookReaderPassword)
                    fbPwd.Password = vm.FacebookReaderPassword ?? "";

                // Facebook bağlantı durumunu güncelle
                UpdateFacebookStatus(vm);
            }
        }

        private void SettingsView_Unloaded(object sender, RoutedEventArgs e)
        {
            // Temizlik
            try
            {
                _facebookChatHost?.Dispose();
                _facebookChatHost = null;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[SettingsView] Unloaded temizlik hatası");
            }
        }

        #region Facebook Status Update

        private void UpdateFacebookStatus(SettingsViewModel vm)
        {
            var statusIndicator = this.FindName("FacebookStatusIndicator") as Ellipse;
            var statusText = this.FindName("TxtFacebookStatus") as TextBlock;
            var connectBtn = this.FindName("BtnFacebookConnect") as Button;
            var logoutBtn = this.FindName("BtnFacebookLogout") as Button;

            if (statusIndicator == null || statusText == null || connectBtn == null || logoutBtn == null)
                return;

            // Bağlantı durumunu kontrol et
            bool isConnected = vm.FacebookReaderConnected &&
                              !string.IsNullOrEmpty(vm.FacebookReaderEmail) &&
                              !string.IsNullOrEmpty(vm.FacebookReaderPassword);

            if (isConnected)
            {
                statusIndicator.Fill = new SolidColorBrush(Color.FromRgb(40, 167, 69)); // Yeşil
                statusText.Text = "Bağlı";
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                connectBtn.Content = "🔄 Yeniden Bağlan";
                logoutBtn.Visibility = Visibility.Visible;
            }
            else if (!string.IsNullOrEmpty(vm.FacebookReaderEmail))
            {
                statusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Sarı
                statusText.Text = "Bilgiler girildi, bağlanılmadı";
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                connectBtn.Content = "🔵 Bağlan";
                logoutBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                statusIndicator.Fill = new SolidColorBrush(Color.FromRgb(128, 128, 128)); // Gri
                statusText.Text = "Bağlı Değil";
                statusText.Foreground = (Brush)FindResource("TextMuted");
                connectBtn.Content = "🔵 Bağlan";
                logoutBtn.Visibility = Visibility.Collapsed;
            }
        }

        private void SetFacebookConnecting()
        {
            var statusIndicator = this.FindName("FacebookStatusIndicator") as Ellipse;
            var statusText = this.FindName("TxtFacebookStatus") as TextBlock;
            var connectBtn = this.FindName("BtnFacebookConnect") as Button;

            if (statusIndicator != null)
                statusIndicator.Fill = new SolidColorBrush(Color.FromRgb(0, 123, 255)); // Mavi

            if (statusText != null)
            {
                statusText.Text = "Bağlanıyor...";
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 123, 255));
            }

            if (connectBtn != null)
            {
                connectBtn.IsEnabled = false;
                connectBtn.Content = "⏳ Bağlanıyor...";
            }
        }

        private void SetFacebookConnectionError(string message)
        {
            var statusIndicator = this.FindName("FacebookStatusIndicator") as Ellipse;
            var statusText = this.FindName("TxtFacebookStatus") as TextBlock;
            var connectBtn = this.FindName("BtnFacebookConnect") as Button;

            if (statusIndicator != null)
                statusIndicator.Fill = new SolidColorBrush(Color.FromRgb(220, 53, 69)); // Kırmızı

            if (statusText != null)
            {
                statusText.Text = message;
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(220, 53, 69));
            }

            if (connectBtn != null)
            {
                connectBtn.IsEnabled = true;
                connectBtn.Content = "🔵 Bağlan";
            }
        }

        #endregion

        #region Password Changed Events

        private void PwdTwitchOAuth_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is SettingsViewModel vm && sender is PasswordBox pb)
            {
                vm.TwitchOAuthToken = pb.Password ?? "";
            }
        }

        private void PwdInstagramSession_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is SettingsViewModel vm && sender is PasswordBox pb)
            {
                vm.InstagramSessionId = pb.Password ?? "";
            }
        }

        private void PwdFacebookReaderPassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is SettingsViewModel vm && sender is PasswordBox pb)
            {
                vm.FacebookReaderPassword = pb.Password ?? "";

                // Şifre değişince bağlantı durumunu sıfırla
                if (vm.FacebookReaderConnected)
                {
                    vm.FacebookReaderConnected = false;
                    UpdateFacebookStatus(vm);
                }
            }
        }

        #endregion

        #region Facebook Connection

        /// <summary>
        /// Facebook okuyucu hesap ile bağlanır.
        /// </summary>
        private async void BtnFacebookConnect_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not SettingsViewModel vm)
                return;

            // Bilgileri kontrol et
            var email = vm.FacebookReaderEmail?.Trim();
            var password = vm.FacebookReaderPassword;

            if (string.IsNullOrEmpty(email))
            {
                MessageBox.Show(
                    "Lütfen okuyucu hesap e-posta/telefon bilgisini girin.",
                    "Eksik Bilgi",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(password))
            {
                MessageBox.Show(
                    "Lütfen okuyucu hesap şifresini girin.",
                    "Eksik Bilgi",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // UI'ı güncelle
            SetFacebookConnecting();

            try
            {
                // FacebookChatHost oluştur (lazy)
                if (_facebookChatHost == null)
                {
                    _facebookChatHost = new FacebookChatHost();

                    // Visual tree'ye ekle (WebView2 için gerekli)
                    // Window'u bul ve oraya ekle
                    var window = Window.GetWindow(this);
                    if (window?.Content is Grid windowGrid)
                    {
                        windowGrid.Children.Add(_facebookChatHost);
                        Log.Debug("[SettingsView] FacebookChatHost Window Grid'e eklendi");
                    }
                    else if (window?.Content is Panel windowPanel)
                    {
                        windowPanel.Children.Add(_facebookChatHost);
                        Log.Debug("[SettingsView] FacebookChatHost Window Panel'e eklendi");
                    }
                    else
                    {
                        // Son çare: StackPanel'e ekle
                        if (this.Content is ScrollViewer sv && sv.Content is Panel panel)
                        {
                            panel.Children.Add(_facebookChatHost);
                            Log.Debug("[SettingsView] FacebookChatHost SettingsView içine eklendi");
                        }
                        else
                        {
                            Log.Warning("[SettingsView] FacebookChatHost için uygun parent bulunamadı!");
                        }
                    }
                }

                // WebView2'yi başlat
                await _facebookChatHost.InitializeAsync();

                // Önce mevcut login durumunu kontrol et
                var alreadyLoggedIn = await _facebookChatHost.CheckLoginStatusAsync();

                if (alreadyLoggedIn)
                {
                    Log.Information("[SettingsView] Facebook zaten giriş yapılmış");
                    vm.FacebookReaderConnected = true;
                    UpdateFacebookStatus(vm);

                    MessageBox.Show(
                        "Facebook hesabı zaten bağlı!",
                        "Başarılı",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                // Login yap
                Log.Information("[SettingsView] Facebook login başlatılıyor: {Email}", email);
                var loginSuccess = await _facebookChatHost.LoginAsync(email, password);

                if (loginSuccess)
                {
                    vm.FacebookReaderConnected = true;
                    UpdateFacebookStatus(vm);

                    MessageBox.Show(
                        "Facebook okuyucu hesap başarıyla bağlandı!\n\n" +
                        "Artık canlı yayın linkini girip chat'i çekebilirsiniz.",
                        "Başarılı",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    vm.FacebookReaderConnected = false;
                    SetFacebookConnectionError("Bağlantı başarısız");

                    MessageBox.Show(
                        "Facebook'a bağlanılamadı.\n\n" +
                        "Olası nedenler:\n" +
                        "• E-posta/şifre yanlış\n" +
                        "• İki faktörlü doğrulama (2FA) gerekiyor\n" +
                        "• Hesap kısıtlanmış veya askıya alınmış\n" +
                        "• Facebook güvenlik kontrolü (checkpoint) istedi\n\n" +
                        "Lütfen bilgileri kontrol edip tekrar deneyin.",
                        "Bağlantı Başarısız",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SettingsView] Facebook bağlantı hatası");

                vm.FacebookReaderConnected = false;
                SetFacebookConnectionError("Hata oluştu");

                MessageBox.Show(
                    $"Facebook bağlantısı sırasında hata oluştu:\n\n{ex.Message}",
                    "Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                // Buton'u tekrar aktif et
                var connectBtn = this.FindName("BtnFacebookConnect") as Button;
                if (connectBtn != null)
                {
                    connectBtn.IsEnabled = true;
                    if (!vm.FacebookReaderConnected)
                        connectBtn.Content = "🔵 Bağlan";
                }
            }
        }

        /// <summary>
        /// Facebook'tan çıkış yapar.
        /// </summary>
        private async void BtnFacebookLogout_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not SettingsViewModel vm)
                return;

            var result = MessageBox.Show(
                "Facebook okuyucu hesap bağlantısını kaldırmak istediğinize emin misiniz?\n\n" +
                "Bu işlem:\n" +
                "• Kayıtlı oturum bilgilerini silecek\n" +
                "• Tekrar bağlanmak için giriş yapmanız gerekecek",
                "Çıkış Yap",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    // WebView2'den çıkış yap
                    if (_facebookChatHost != null)
                    {
                        await _facebookChatHost.LogoutAsync();
                    }

                    // ViewModel'i temizle
                    vm.FacebookReaderConnected = false;
                    // Email ve password'u silmiyoruz, sadece bağlantı durumunu

                    UpdateFacebookStatus(vm);

                    MessageBox.Show(
                        "Facebook bağlantısı kaldırıldı.",
                        "Çıkış Yapıldı",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[SettingsView] Facebook logout hatası");

                    // Yine de temizle
                    vm.FacebookReaderConnected = false;
                    UpdateFacebookStatus(vm);
                }
            }
        }

        #endregion

        #region Legacy Facebook Login (Deprecated - Backward Compatibility)

        // Bu metod artık kullanılmıyor ama eski kodla uyumluluk için tutuluyor
        [Obsolete("BtnFacebookLogin_Click artık kullanılmıyor. BtnFacebookConnect_Click kullanın.")]
        private void BtnFacebookLogin_Click(object sender, RoutedEventArgs e)
        {
            // Eski metodu yeni metoda yönlendir
            BtnFacebookConnect_Click(sender, e);
        }

        #endregion
    }
}