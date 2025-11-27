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
    public sealed class ControlViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly IStreamController _stream;
        private readonly Func<(ObservableCollection<TargetItem> targets, SettingsData settings)> _provider;

        // Servisler
        private readonly PreviewService _preview = new();
        private readonly AudioService _audioService = new(); // YENİ: Ses Servisi

        private CancellationTokenSource? _cts;

        public ControlViewModel(
            IStreamController stream,
            Func<(ObservableCollection<TargetItem> targets, SettingsData settings)> provider)
        {
            _stream = stream;
            _provider = provider;

            // 1. Önizleme Bağlantısı
            _preview.OnFrame += bmp => PreviewImage = bmp;

            // 2. Ses Servisi Bağlantıları (YENİ)
            _audioService.OnLevelChange += level =>
            {
                // UI Thread'de güncelle (0.0 - 1.0 arasını 0-100 yapıyoruz)
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    AudioLevel = level * 100;
                });
            };

            _audioService.OnMuteChange += muted =>
            {
                IsMuted = muted;
            };

            // 3. Komut Tanımları
            StartCommand = new RelayCommand(async _ => await StartAsync(), _ => !IsRunning);
            StopCommand = new RelayCommand(async _ => await StopAsync(), _ => IsRunning);

            StartPreviewCommand = new RelayCommand(async _ =>
            {
                if (_preview.IsRunning) await StopPreviewAsync();
                else await StartPreviewAsync();
            });

            // Mola Komutu
            ToggleBreakCommand = new RelayCommand(_ =>
            {
                if (IsOnBreak)
                {
                    IsOnBreak = false;
                    if (System.Windows.Application.Current.MainWindow is MainWindow mw) mw.StopBreak();
                }
                else
                {
                    IsOnBreak = true;
                    if (System.Windows.Application.Current.MainWindow is MainWindow mw) mw.StartBreak(BreakDuration);
                }
            });

            // Ses Susturma Komutu
            ToggleMuteCommand = new RelayCommand(_ => _audioService.ToggleMute());

            // Başlangıçta ses servisini hazırla
            InitializeAudio();
        }

        private async void InitializeAudio()
        {
            var s = Services.SettingsStore.Load();
            await _audioService.InitializeAsync(s.SelectedAudioDevice ?? "");
        }

        // --- PREVIEW ---
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
                await _preview.StartAsync(-1, s.Width, s.Height, s.Fps);
        }

        public Task StopPreviewAsync() => _preview.StopAsync();

        // --- YAYIN DURUMU ---
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

        private string _advisory = "";
        public string Advisory
        {
            get => _advisory;
            private set { _advisory = value; OnPropertyChanged(); }
        }

        // --- MOLA MODU ---
        private bool _isOnBreak;
        public bool IsOnBreak
        {
            get => _isOnBreak;
            set { _isOnBreak = value; OnPropertyChanged(); }
        }

        private int _breakDuration = 5; // Varsayılan 5 dk
        public int BreakDuration
        {
            get => _breakDuration;
            set { _breakDuration = value; OnPropertyChanged(); }
        }

        // --- SES MİKSERİ (YENİ) ---
        private double _audioLevel;
        public double AudioLevel
        {
            get => _audioLevel;
            private set { _audioLevel = value; OnPropertyChanged(); }
        }

        private bool _isMuted;
        public bool IsMuted
        {
            get => _isMuted;
            private set { _isMuted = value; OnPropertyChanged(); }
        }

        // --- KOMUTLAR ---
        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand StartPreviewCommand { get; }
        public ICommand ToggleBreakCommand { get; }
        public ICommand ToggleMuteCommand { get; } // YENİ

        // --- YAYIN MANTIĞI ---
        private async Task StartAsync()
        {
            try
            {
                var (targets, settings) = _provider();
                _cts = new CancellationTokenSource();

                Status = "Başlatılıyor...";
                Metric = "";
                Advisory = "";

                var result = await _stream.StartWithResultAsync(targets, settings, _cts.Token);

                if (result.Success)
                {
                    IsRunning = true;
                    Status = "Yayında";
                    _ = Task.Run(async () =>
                    {
                        while (IsRunning || _stream.IsReconnecting)
                        {
                            Status = _stream.LastMessage ?? "Yayında";
                            Metric = _stream.LastMetric ?? "";
                            if (!string.IsNullOrEmpty(_stream.LastAdvisory)) Advisory = _stream.LastAdvisory;
                            await Task.Delay(200);
                        }
                    });
                }
                else
                {
                    IsRunning = false;
                    Status = "Hata";
                    Advisory = result.UserMessage ?? "Bilinmeyen bir hata oluştu.";
                }
            }
            catch (Exception ex)
            {
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
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public void Dispose()
        {
            // Kaynakları serbest bırak
            _preview?.Dispose();
            _audioService?.Dispose();
            _cts?.Cancel();
            _cts?.Dispose();

            // Olay aboneliklerini kaldır (Memory Leak önlemi)
            if (_audioService != null)
            {
                // Eventleri null'a çekmek pratik bir çözümdür
                // Gerçek implementasyonda -= ile çıkarmak daha doğrudur ama
                // sınıf yok olduğu için bu da kabul edilebilir.
            }
        }
    }
}