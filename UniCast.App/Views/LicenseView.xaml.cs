using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UniCast.App.ViewModels;
using UniCast.Licensing.Models;
using Brushes = System.Windows.Media.Brushes;
using UserControl = System.Windows.Controls.UserControl;

namespace UniCast.App.Views
{
    /// <summary>
    /// Lisans görünümü
    /// </summary>
    public partial class LicenseView : UserControl
    {
        private LicenseViewModel? _viewModel;

        public LicenseView()
        {
            InitializeComponent();
            Loaded += LicenseView_Loaded;
        }

        private void LicenseView_Loaded(object sender, RoutedEventArgs e)
        {
            _viewModel = DataContext as LicenseViewModel;
            UpdateStatusIndicator();
        }

        private void UpdateStatusIndicator()
        {
            if (_viewModel == null) return;

            StatusIndicator.Fill = _viewModel.IsLicensed
                ? Brushes.LimeGreen
                : Brushes.Orange;
        }

        private void BtnActivate_Click(object sender, RoutedEventArgs e)
        {
            var activationWindow = new ActivationWindow();
            activationWindow.Owner = Window.GetWindow(this);

            if (activationWindow.ShowDialog() == true)
            {
                _viewModel?.RefreshLicense();
                UpdateStatusIndicator();
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            _viewModel?.RefreshLicense();
            UpdateStatusIndicator();
        }
    }
}