using System.Windows;
using UniCast.App.Services;
using UniCast.App.ViewModels;
using UniCast.App.Views;

namespace UniCast.App
{
    public partial class MainWindow : Window
    {
        private readonly IDeviceService _deviceService;
        private readonly IStreamController _stream;
        private readonly SettingsViewModel _settingsVm;
        private readonly TargetsViewModel _targetsVm;
        private readonly ControlViewModel _controlVm;

        public MainWindow()
        {
            InitializeComponent();

            _deviceService = new DeviceService();
            _stream = new StreamController();

            _settingsVm = new SettingsViewModel(_deviceService);
            _targetsVm = new TargetsViewModel();

            _controlVm = new ControlViewModel(
                _stream,
                () =>
                {
                    var settings = Services.SettingsStore.Load();
                    return (_targetsVm.Targets, settings);
                });

            // Varsayılan ekran
            MainContent.Content = new ControlView(_controlVm);

            // Menü navigasyonu (TargetsView ctor TargetsViewModel bekliyor)
            BtnControl.Click += (_, __) => MainContent.Content = new ControlView(_controlVm);
            BtnTargets.Click += (_, __) => MainContent.Content = new TargetsView(_targetsVm);
            BtnSettings.Click += (_, __) => MainContent.Content = new SettingsView { DataContext = _settingsVm };
            BtnPreview.Click += (_, __) => MainContent.Content = new PreviewView(new PreviewViewModel());

        }
    }
}
