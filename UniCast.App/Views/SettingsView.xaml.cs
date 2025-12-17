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
            }
        }

        private void PwdTwitchOAuth_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is SettingsViewModel vm && sender is PasswordBox pb)
            {
                vm.TwitchOAuthToken = pb.Password ?? "";
            }
        }
    }
}