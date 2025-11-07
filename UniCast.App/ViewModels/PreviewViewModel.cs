using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using UniCast.App.Infrastructure;
using UniCast.App.Services;
using UniCast.Core.Settings;

namespace UniCast.App.ViewModels
{
    public sealed class PreviewViewModel : INotifyPropertyChanged
    {
        private readonly PreviewService _service = new();
        private ImageSource? _image;
        private bool _isStarting;

        public ImageSource? Image
        {
            get => _image;
            private set { _image = value; OnPropertyChanged(); }
        }

        public ICommand StartPreviewCommand { get; }
        public ICommand StopPreviewCommand { get; }

        public PreviewViewModel()
        {
            // PreviewService, dondurulmuş (Freeze) BitmapSource döndürüyorsa
            // UI thread'e dispatch etmeye gerek yok.
            _service.OnFrame += frame => { Image = frame; };

            StartPreviewCommand = new RelayCommand(async _ =>
            {
                if (_isStarting) return;
                _isStarting = true;
                try
                {
                    SettingsData  s = Services.SettingsStore.Load();
                    await _service.StartAsync(-1, s.Width, s.Height, s.Fps);
                }
                finally
                {
                    _isStarting = false;
                }
            });

            StopPreviewCommand = new RelayCommand(async _ =>
            {
                await _service.StopAsync();
            });
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
