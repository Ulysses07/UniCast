using System.Windows;
using System.Windows.Controls;
using UniCast.App.ViewModels;

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

                // Facebook
                var fbPwd = this.FindName("PwdFacebookToken") as PasswordBox;
                if (fbPwd != null && fbPwd.Password != vm.FacebookAccessToken)
                    fbPwd.Password = vm.FacebookAccessToken ?? "";
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

        private void PwdFacebookToken_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is SettingsViewModel vm && sender is PasswordBox pb)
            {
                vm.FacebookAccessToken = pb.Password ?? "";
            }
        }
    }
}