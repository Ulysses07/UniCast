using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UniCast.App.Infrastructure;
using UniCast.App.Services;
using UniCast.Core.Settings;

namespace UniCast.App.ViewModels
{
    public sealed class PreviewViewModel : INotifyPropertyChanged
    {
        private readonly PreviewService _service = new();
        private ImageSource? _image;

        public ImageSource? Image
        {
            get => _image;
            private set { _image = value; OnPropertyChanged(); }
        }

        public ICommand StartPreviewCommand { get; }
        public ICommand StopPreviewCommand { get; }

        public PreviewViewModel()
        {
            _service.OnFrame += frame =>
            {
                // UI thread'e marshall etmeye gerek kalmasın diye Freeze ettik
                Image = frame;
            };

            StartPreviewCommand = new RelayCommand(async _ =>
            {
                // İstersen Settings'ten çözünürlük/fps çekebiliriz
                SettingsData s = Services.SettingsStore.Load();
                await _service.StartAsync(preferredIndex: -1, width: s.Width, height: s.Height, fps: s.Fps);
            });

            StopPreviewCommand = new RelayCommand(async _ => await _service.StopAsync());
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
