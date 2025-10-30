namespace UniCast.App.Views
{
    public partial class SettingsView : System.Windows.Controls.UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
        }

        public SettingsView(UniCast.App.ViewModels.SettingsViewModel vm) : this()
        {
            DataContext = vm;
        }
    }
}
