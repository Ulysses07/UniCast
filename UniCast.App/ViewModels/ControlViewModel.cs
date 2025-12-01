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
using UniCast.Core.Models;
using UniCast.Core.Services;


// App.Services.SettingsData kullan
using SettingsData = UniCast.App.Services.SettingsData;

namespace UniCast.App.ViewModels
{
    public sealed class ControlViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly IStreamController _stream;
        private readonly Func<(ObservableCollection<TargetItem> targets, SettingsData settings)>? _provider;

        private readonly PreviewService _preview = new();
        private readonly AudioService _audioService = new();

        private CancellationTokenSource? _cts;
        private bool _disposed;

        // DÜZELTME: Event handler'ları field olarak tut (unsubscribe için)
        private readonly Action<ImageSource> _onFrameHandler;
        private readonly Action<float> _onLevelChangeHandler;
        private readonly Action<bool> _onMuteChangeHandler;

        // Parametresiz constructor
        public ControlViewModel() : this(
            StreamController.Instance,
            () => (new ObservableCollection<TargetItem>(), SettingsStore.Data))
        {
        }

        public ControlViewModel(
            IStreamController stream,
            Func<(ObservableCollection<TargetItem> targets, SettingsData settings)> provider)
        {
            _stream = stream;
            _provider = provider;

            // DÜZELTME: Handler'ları field'lara ata
            _onFrameHandler = bmp => PreviewImage = bmp;
            _onLevelChangeHandler = level =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    AudioLevel = level * 100;
                });
            };
            _onMuteChangeHandler = muted => IsMuted = muted;

            // Event'lere subscribe ol
            _preview.OnFrame += _onFrameHandler;
            _audioService.OnLevelChange += _onLevelChangeHandler;
            _audioService.OnMuteChange += _onMuteChangeHandler;

            // Komut Tanımları
            StartCommand = new RelayCommand(async _ => await StartAsync(), _ => !IsRunning);
            StopCommand = new RelayCommand(async _ => await StopAsync(), _ => IsRunning);

            StartPreviewCommand = new RelayCommand(async _ =>
            {
                if (_preview.IsRunning) await StopPreviewAsync();
                else await StartPreviewAsync();
            });

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

            ToggleMuteCommand = new RelayCommand(_ => _audioService.ToggleMute());

            _ = InitializeAudio();
        }

        private async Task InitializeAudio()
        {
            var s = Services.SettingsStore.Data;
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
            var s = Services.SettingsStore.Data;
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

        private int _breakDuration = 5;
        public int BreakDuration
        {
            get => _breakDuration;
            set { _breakDuration = value; OnPropertyChanged(); }
        }

        // --- SES MİKSERİ ---
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
        public ICommand ToggleMuteCommand { get; }

        // --- YAYIN MANTIĞI ---
        private async Task StartAsync()
        {
            if (_disposed) return;

            try
            {
                var (targets, settings) = _provider();

                // DÜZELTME: Eski CTS'i dispose et
                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                Status = "Başlatılıyor...";
                Metric = "";
                Advisory = "";

                var result = await _stream.StartWithResultAsync(targets, settings, _cts.Token);

                if (result.Success)
                {
                    IsRunning = true;
                    Status = "Yayında";

                    // DÜZELTME: Token'ı kullan ve task'ı takip etme (fire-and-forget ama kontrollü)
                    var token = _cts.Token;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            while ((IsRunning || _stream.IsReconnecting) && !token.IsCancellationRequested)
                            {
                                Status = _stream.LastMessage ?? "Yayında";
                                Metric = _stream.LastMetric ?? "";
                                if (!string.IsNullOrEmpty(_stream.LastAdvisory)) Advisory = _stream.LastAdvisory;
                                await Task.Delay(200, token);
                            }
                        }
                        catch (OperationCanceledException) { }
                    }, token);
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
            if (_disposed) return;

            try
            {
                Status = "Durduruluyor...";

                // DÜZELTME: CTS'i iptal et
                try
                {
                    _cts?.Cancel();
                }
                catch { }

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
            if (_disposed) return;
            _disposed = true;

            // DÜZELTME: Event handler'ları unsubscribe et
            _preview.OnFrame -= _onFrameHandler;
            _audioService.OnLevelChange -= _onLevelChangeHandler;
            _audioService.OnMuteChange -= _onMuteChangeHandler;

            // Servisleri dispose et
            _preview.Dispose();
            _audioService.Dispose();

            // CTS'i temizle
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
            }
            catch { }
            _cts = null;

            // PropertyChanged'i temizle
            PropertyChanged = null;
        }
    }
}