using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using UniCast.App.Infrastructure;
using UniCast.App.Services;
using UniCast.Core.Models;   // StreamStartResult için
using UniCast.Core.Settings;

namespace UniCast.App.ViewModels
{
    public sealed class ControlViewModel : INotifyPropertyChanged
    {
        private readonly IStreamController _stream;
        private readonly Func<(ObservableCollection<TargetItem> targets, SettingsData settings)> _provider;
        private readonly PreviewService _preview = new();
        private CancellationTokenSource? _cts;

        public ControlViewModel(
            IStreamController stream,
            Func<(ObservableCollection<TargetItem> targets, SettingsData settings)> provider)
        {
            _stream = stream;
            _provider = provider;

            _preview.OnFrame += bmp => PreviewImage = bmp;

            StartCommand = new RelayCommand(async _ => await StartAsync(), _ => !IsRunning);
            StopCommand = new RelayCommand(async _ => await StopAsync(), _ => IsRunning);
        }

        // --- Preview ---
        private ImageSource? _previewImage;
        public ImageSource? PreviewImage
        {
            get => _previewImage;
            private set { _previewImage = value; OnPropertyChanged(); }
        }

        public async Task StartPreviewAsync()
        {
            var s = Services.SettingsStore.Load();
            if (!_preview.IsRunning)
            {
                await _preview.StartAsync(-1, s.Width, s.Height, s.Fps);
            }
        }

        public Task StopPreviewAsync() => _preview.StopAsync();

        // --- Stream state ---
        private string _status = "Hazır";
        public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }

        private string _metric = "";
        public string Metric { get => _metric; private set { _metric = value; OnPropertyChanged(); } }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                _isRunning = value;
                OnPropertyChanged();
                (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        // --- Advisory: Kullanıcıya Hata/Uyarı Gösterilen Alan ---
        private string _advisory = "";
        public string Advisory
        {
            get => _advisory;
            private set { _advisory = value; OnPropertyChanged(); }
        }

        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }

        private async Task StartAsync()
        {
            try
            {
                // 1. Hazırlık
                var (targets, settings) = _provider();
                _cts = new CancellationTokenSource();

                Status = "Başlatılıyor...";
                Metric = "";
                Advisory = ""; // Önceki hataları temizle

                // 2. Başlatma İsteği (Result Pattern Kullanımı)
                // Artık try-catch yerine sonucu if-else ile kontrol ediyoruz
                var result = await _stream.StartWithResultAsync(targets, settings, _cts.Token);

                if (result.Success)
                {
                    // --- BAŞARILI ---
                    IsRunning = true;
                    Status = "Yayında";

                    // Arka plan durum takipçisi (Status Updater)
                    _ = Task.Run(async () =>
                    {
                        while (IsRunning || _stream.IsReconnecting)
                        {
                            Status = _stream.LastMessage ?? "Yayında";
                            Metric = _stream.LastMetric ?? "";

                            // Eğer yayın sırasında bağlantı koparsa (Reconnect uyarısı vb.) onu da yansıt
                            if (!string.IsNullOrEmpty(_stream.LastAdvisory))
                                Advisory = _stream.LastAdvisory;

                            await Task.Delay(200);
                        }
                    });
                }
                else
                {
                    // --- BAŞARISIZ (Hata Yönetimi) ---
                    IsRunning = false;
                    Status = "Hata";

                    // Kullanıcıya "neden olmadığını" Türkçe ve net bir dille yazıyoruz
                    Advisory = result.UserMessage ?? "Bilinmeyen bir hata oluştu.";
                }
            }
            catch (Exception ex)
            {
                // Beklenmedik "Crash" durumları için son güvenlik ağı
                Status = "Kritik Hata";
                Advisory = $"Beklenmedik hata: {ex.Message}";
                IsRunning = false;
            }
        }

        private async Task StopAsync()
        {
            try
            {
                Status = "Durduruluyor...";
                _cts?.Cancel();
                await _stream.StopAsync();
            }
            catch (Exception ex)
            {
                Status = "Durdurma Hatası";
                Advisory = ex.Message;
            }
            finally
            {
                IsRunning = false;
                Status = "Durduruldu";
                Metric = "";
                // Advisory'i temizlemiyoruz, belki durdurma nedenini görmek ister
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}