using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using UniCast.App.Infrastructure;
using UniCast.App.Services;
using Serilog;

namespace UniCast.App.ViewModels
{
    public sealed class PreviewViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly PreviewService _service;
        private bool _disposed;

        private readonly Action<ImageSource> _onFrameHandler;

        private ImageSource? _previewImage;
        public ImageSource? PreviewImage
        {
            get => _previewImage;
            private set { _previewImage = value; OnPropertyChanged(); }
        }

        private bool _isStarting;

        public ICommand StartPreviewCommand { get; }
        public ICommand StopPreviewCommand { get; }

        // Parametresiz constructor
        public PreviewViewModel() : this(new PreviewService())
        {
        }

        // DÜZELTME: DI Constructor
        public PreviewViewModel(PreviewService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));

            _onFrameHandler = frame => PreviewImage = frame;
            _service.OnFrame += _onFrameHandler;

            StartPreviewCommand = new RelayCommand(async _ =>
            {
                if (_isStarting || _disposed) return;
                _isStarting = true;
                try
                {
                    var s = SettingsStore.Data;
                    await _service.StartAsync(-1, s.Width, s.Height, s.Fps, s.CameraRotation);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Preview başlatma hatası");
                }
                finally
                {
                    _isStarting = false;
                }
            });

            StopPreviewCommand = new RelayCommand(async _ =>
            {
                try
                {
                    await _service.StopAsync();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Preview durdurma hatası");
                }
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _service.OnFrame -= _onFrameHandler;
            // Service'i dispose etmiyoruz - DI container yönetecek

            PropertyChanged = null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}