using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using UniCast.App.Infrastructure;
using UniCast.App.Services;
using UniCast.Core.Settings;
using Serilog;

namespace UniCast.App.ViewModels
{
    // DÜZELTME: IDisposable eklendi
    public sealed class PreviewViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly PreviewService _service;
        private bool _disposed;

        // DÜZELTME: Event handler'ı field olarak tut (unsubscribe için)
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

        public PreviewViewModel()
        {
            _service = new PreviewService();

            // Handler'ı field'a ata
            _onFrameHandler = frame => PreviewImage = frame;
            _service.OnFrame += _onFrameHandler;

            StartPreviewCommand = new RelayCommand(async _ =>
            {
                if (_isStarting || _disposed) return;
                _isStarting = true;
                try
                {
                    SettingsData s = Services.SettingsStore.Load();
                    await _service.StartAsync(-1, s.Width, s.Height, s.Fps);
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

        // DÜZELTME: Dispose metodu eklendi
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Event handler'ı kaldır
            _service.OnFrame -= _onFrameHandler;

            // Servisi dispose et
            _service.Dispose();

            // PropertyChanged'i temizle
            PropertyChanged = null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}