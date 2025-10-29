using System.Windows;
using System.Windows.Controls;
using UniCast.App.ViewModels;

namespace UniCast.App.Views
{
    public partial class SettingsView : UserControl
    {
        private readonly SettingsViewModel _vm;

        // Parametresiz kurucu (tasarım desteği / bağımsız kullanım)
        public SettingsView()
        {
            InitializeComponent();
            _vm = new SettingsViewModel();
            DataContext = _vm;
        }

        // MainWindow’dan mevcut VM ile kullanmak için
        public SettingsView(SettingsViewModel vm)
        {
            InitializeComponent();
            _vm = vm ?? new SettingsViewModel();
            DataContext = _vm;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            _vm.Save();
            MessageBox.Show("Ayarlar kaydedildi.", "UniCast", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
