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
                var igPwd = this.FindName("PwdInstagramSession") as PasswordBox;
                if (igPwd != null && igPwd.Password != vm.InstagramSessionId)
                    igPwd.Password = vm.InstagramSessionId ?? "";

                var fbPwd = this.FindName("PwdFacebookToken") as PasswordBox;
                if (fbPwd != null && fbPwd.Password != vm.FacebookAccessToken)
                    fbPwd.Password = vm.FacebookAccessToken ?? "";
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
