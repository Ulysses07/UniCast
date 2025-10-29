using System.Windows;
using UniCast.App.ViewModels;
using UniCast.App.Views;
using UniCast.App.Services;
using UniCast.Encoder;

namespace UniCast.App
{
    public partial class MainWindow : Window
    {
        private readonly FfmpegProcess _encoder;
        private readonly ControlViewModel _controlVm;
        private readonly TargetsViewModel _targetsVm;
        private readonly SettingsViewModel _settingsVm;
        private readonly StreamController _stream;

        public MainWindow()
        {
            InitializeComponent();

            _encoder = new FfmpegProcess();
            _controlVm = new ControlViewModel(_encoder);
            _targetsVm = new TargetsViewModel();
            _settingsVm = new SettingsViewModel();
            _stream = new StreamController(_controlVm, _targetsVm);

            ShowControl();
        }

        private void ShowControl()
        {
            MainContent.Content = new ControlView(_controlVm, _targetsVm);
            TxtTopStatus.Text = $"Durum: {_controlVm.Status}";
            _encoder.OnMetrics += m =>
            {
                Dispatcher.Invoke(() =>
                {
                    TxtTopStatus.Text = $"Durum: {_controlVm.Status} • FPS: {m.Fps:0} • Video: {m.VideoKbps:0} kbps";
                });
            };
        }

        private void ShowTargets()
        {
            MainContent.Content = new TargetsView(_targetsVm);
            TxtTopStatus.Text = "RTMP/RTMPS hedeflerini düzenleyin";
        }

        private void ShowSettings()
        {
            // Artık SettingsView(SettingsViewModel) kurucusu var
            MainContent.Content = new SettingsView(_settingsVm);
            TxtTopStatus.Text = "Uygulama ayarları";
        }

        private void BtnNavControl_Click(object sender, RoutedEventArgs e) => ShowControl();
        private void BtnNavTargets_Click(object sender, RoutedEventArgs e) => ShowTargets();
        private void BtnSettings_Click(object sender, RoutedEventArgs e) => ShowSettings();

        protected override void OnClosed(System.EventArgs e)
        {
            base.OnClosed(e);
            try { _controlVm?.Dispose(); } catch { }
            try { _encoder?.Dispose(); } catch { }
        }
    }
}
