using System.Windows;
using System.Windows.Controls;
using UniCast.App.ViewModels;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace UniCast.App.Views
{
    public partial class SettingsView : System.Windows.Controls.UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
            this.Loaded += SettingsView_Loaded;
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

                // Facebook bağlantı durumunu güncelle
                UpdateFacebookStatus(vm);
            }
        }

        private void UpdateFacebookStatus(SettingsViewModel vm)
        {
            var statusText = this.FindName("TxtFacebookStatus") as TextBlock;
            var loginBtn = this.FindName("BtnFacebookLogin") as Button;
            var logoutBtn = this.FindName("BtnFacebookLogout") as Button;

            if (statusText != null && loginBtn != null && logoutBtn != null)
            {
                if (!string.IsNullOrEmpty(vm.FacebookCookies))
                {
                    statusText.Text = "🟢 Bağlı";
                    loginBtn.Content = "🔄 Yeniden Bağlan";
                    logoutBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    statusText.Text = "⚪ Bağlı Değil";
                    loginBtn.Content = "🔵 Facebook'a Giriş Yap";
                    logoutBtn.Visibility = Visibility.Collapsed;
                }
            }
        }

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

        private void BtnFacebookLogin_Click(object sender, RoutedEventArgs e)
        {
            var loginWindow = new FacebookLoginWindow
            {
                Owner = Window.GetWindow(this)
            };

            var result = loginWindow.ShowDialog();

            if (result == true && DataContext is SettingsViewModel vm)
            {
                // Cookie'leri kaydet
                vm.FacebookCookies = loginWindow.FacebookCookies ?? "";
                vm.FacebookUserId = loginWindow.FacebookUserId ?? "";

                // UI'ı güncelle
                UpdateFacebookStatus(vm);
            }
        }

        private void BtnFacebookLogout_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is SettingsViewModel vm)
            {
                var result = MessageBox.Show(
                    "Facebook bağlantısını kaldırmak istediğinize emin misiniz?",
                    "Çıkış",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    vm.FacebookCookies = "";
                    vm.FacebookUserId = "";
                    UpdateFacebookStatus(vm);
                }
            }
        }
    }
}