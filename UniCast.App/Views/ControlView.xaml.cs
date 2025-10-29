using System.Linq;
using System.Windows;
using System.Windows.Controls;
using UniCast.App.Services;
using UniCast.App.ViewModels;

namespace UniCast.App.Views
{
    public partial class ControlView : UserControl
    {
        private readonly ControlViewModel _vm;
        private readonly TargetsViewModel _targetsVm;

        public ControlView(ControlViewModel vm, TargetsViewModel targetsVm)
        {
            InitializeComponent();
            _vm = vm;
            _targetsVm = targetsVm;
            DataContext = _vm;

            // Cihaz listeleri
            try
            {
                var (video, audio) = DeviceService.ListDshowDevices();
                CmbVideoDevices.ItemsSource = video;
                CmbAudioDevices.ItemsSource = audio;
                if (video.Length > 0) CmbVideoDevices.SelectedIndex = 0;
                if (audio.Length > 0) CmbAudioDevices.SelectedIndex = 0;
            }
            catch { /* ffmpeg yoksa sessiz geç */ }

            // Preset combobox değişimi
            CmbPreset.SelectionChanged += (_, __) =>
            {
                var item = CmbPreset.SelectedItem as ComboBoxItem;
                var tag = item?.Tag as string; // "WxHxFPSxVKbpsxAKbps"
                if (string.IsNullOrWhiteSpace(tag)) return;
                var parts = tag.Split('x');
                if (parts.Length == 5 &&
                    int.TryParse(parts[0], out var w) &&
                    int.TryParse(parts[1], out var h) &&
                    int.TryParse(parts[2], out var f) &&
                    int.TryParse(parts[3], out var v) &&
                    int.TryParse(parts[4], out var a))
                {
                    _vm.Preset = new Core.EncoderProfile(
                        item?.Content?.ToString() ?? "Preset", // name parametresi eklendi
                        w, h, f, v, a
                    );
                }
            };

            StartBtn.Click += async (_, __) =>
            {
                _vm.SelectedCamera = CmbVideoDevices.SelectedItem as string;
                _vm.SelectedMic = CmbAudioDevices.SelectedItem as string;

                var urls = _targetsVm.GetEnabledUrls().ToList();
                await _vm.StartAsync(urls);
            };

            StopBtn.Click += async (_, __) => await _vm.StopAsync();
        }
    }
}
