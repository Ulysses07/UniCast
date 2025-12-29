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
using UniCast.App.Services.Capture;
using UniCast.Core.Models;


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
        // DÜZELTME v27: Status monitor task referansı
        private Task? _statusMonitorTask;
        private bool _disposed;

        // Events for stream state changes
        public event Action<ObservableCollection<TargetItem>>? StreamStarting;  // Yayın başlamadan önce (chat overlay için)
        public event Action<ObservableCollection<TargetItem>>? StreamStarted;   // Yayın başladıktan sonra
        public event Action? StreamStopped;

        // DÜZELTME: Event handler'ları field olarak tut (unsubscribe için)
        private readonly Action<ImageSource> _onFrameHandler;
        private readonly Action<float> _onLevelChangeHandler;
        private readonly Action<bool> _onMuteChangeHandler;

        // Parametresiz constructor
        // DÜZELTME: StreamControllerAdapter kullanılıyor
        private static readonly Lazy<StreamControllerAdapter> _defaultAdapter = new(() => new StreamControllerAdapter());

        public ControlViewModel() : this(
            _defaultAdapter.Value,
            () => (new ObservableCollection<TargetItem>(TargetsStore.Load()), SettingsStore.Data))
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
                // DÜZELTME v55: BeginInvoke kullan (non-blocking)
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    AudioLevel = level * 100;
                }));
            };
            _onMuteChangeHandler = muted =>
            {
                IsMuted = muted;
                if (muted)
                    Services.ToastService.Instance.ShowInfo("🔇 Mikrofon kapatıldı");
                else
                    Services.ToastService.Instance.ShowInfo("🎤 Mikrofon açıldı");
            };

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

            // Audio başlatmayı güvenli şekilde yap
            _ = InitializeAudioSafe();
        }

        private async Task InitializeAudioSafe()
        {
            try
            {
                await InitializeAudio().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[ControlViewModel] Audio başlatma hatası: {Message}", ex.Message);
                // Audio olmadan da devam edilebilir
            }
        }

        private async Task InitializeAudio()
        {
            var s = Services.SettingsStore.Data;
            await _audioService.InitializeAsync(s.SelectedAudioDevice ?? "").ConfigureAwait(false);
        }

        // --- PREVIEW ---
        private ImageSource? _previewImage;
        public ImageSource? PreviewImage
        {
            get => _previewImage;
            private set { _previewImage = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// DÜZELTME v55: Tüm preview başlatma işlemi arka planda
        /// - UI thread hiç bloklanmaz
        /// - Kamera açılışı 30 saniye sürse bile UI responsive kalır
        /// </summary>
        public Task StartPreviewAsync()
        {
            var s = Services.SettingsStore.Data;
            if (_preview.IsRunning) return Task.CompletedTask;

            // DÜZELTME v55: Fire-and-forget - UI thread'i hiç beklemesin
            // Tüm işlem arka planda yapılır, UI hemen serbest kalır
            _ = Task.Run(async () =>
            {
                try
                {
                    // 1. Kamera index'i bul (FFmpeg process çalıştırır)
                    int cameraIndex = await GetCameraIndexAsync(s.SelectedVideoDevice ?? s.DefaultCamera)
                        .ConfigureAwait(false);

                    // 2. Preview'ı başlat (VideoCapture oluşturma dahil - yavaş işlem)
                    await _preview.StartAsync(cameraIndex, s.Width, s.Height, s.Fps)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ControlViewModel] StartPreviewAsync hatası: {ex.Message}");
                }
            });

            return Task.CompletedTask;
        }

        // DÜZELTME v50: Kamera adından index bulma metodu
        private async Task<int> GetCameraIndexAsync(string? deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return 0;

            try
            {
                var deviceService = new DeviceService();
                var devices = await deviceService.GetVideoDevicesAsync().ConfigureAwait(false);

                for (int i = 0; i < devices.Count; i++)
                {
                    if (devices[i].Name == deviceName)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ControlViewModel] Kamera bulundu: {deviceName} -> index {i}");
                        return i;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[ControlViewModel] Kamera bulunamadı: {deviceName}, varsayılan kullanılıyor");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ControlViewModel] Kamera index hatası: {ex.Message}");
            }

            return 0; // Varsayılan: ilk kamera
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

        // Loading durumu (yayın başlatılırken/durdurulurken)
        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                _isLoading = value;
                OnPropertyChanged();
                (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        // Yayın Süresi Sayacı
        private DateTime _streamStartTime;
        private System.Windows.Threading.DispatcherTimer? _streamTimer;

        private string _streamDuration = "00:00:00";
        public string StreamDuration
        {
            get => _streamDuration;
            private set { _streamDuration = value; OnPropertyChanged(); }
        }

        private void StartStreamTimer()
        {
            _streamStartTime = DateTime.Now;
            _streamTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _streamTimer.Tick += (s, e) =>
            {
                var elapsed = DateTime.Now - _streamStartTime;
                StreamDuration = elapsed.ToString(@"hh\:mm\:ss");
            };
            _streamTimer.Start();
        }

        private void StopStreamTimer()
        {
            _streamTimer?.Stop();
            _streamTimer = null;
            StreamDuration = "00:00:00";
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
                // Loading başlat
                IsLoading = true;

                // DÜZELTME: Null check eklendi
                if (_provider == null)
                {
                    Advisory = "Yapılandırma sağlayıcısı bulunamadı.";
                    return;
                }

                var (targets, settings) = _provider();

                // DÜZELTME: Eski CTS'i dispose et
                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                Status = "Başlatılıyor...";
                Metric = "";
                Advisory = "";
                
                // Preview çalışıyorsa, pipe streaming'i başlat
                if (_preview.IsRunning)
                {
                    Status = "Video pipe hazırlanıyor...";
                    await _preview.StartStreamingAsync(_cts.Token);
                    
                    var pipeName = _preview.GetPipeName();
                    if (!string.IsNullOrEmpty(pipeName))
                    {
                        // Rotation uygulandıktan sonraki boyutları hesapla
                        var s = Services.SettingsStore.Data;
                        int width = s.Width;
                        int height = s.Height;
                        
                        // 90° veya 270° rotation varsa boyutlar yer değiştirir
                        if (s.CameraRotation == 90 || s.CameraRotation == 270)
                        {
                            width = s.Height;
                            height = s.Width;
                        }
                        
                        _stream.SetPipeInput(pipeName, width, height, s.Fps);
                        System.Diagnostics.Debug.WriteLine($"[ControlViewModel] Pipe input aktif: {pipeName}, {width}x{height}");
                    }
                }
                else
                {
                    // Preview çalışmıyorsa pipe kullanma
                    _stream.ClearPipeInput();
                }
                
                // Chat overlay ve diğer servislerin başlaması için event fire et
                // (MainWindow bu event'i dinleyip chat overlay'i başlatacak)
                StreamStarting?.Invoke(targets);
                
                // Chat overlay pipe'ının hazır olması için kısa bir süre bekle
                var chatOverlayEnabled = Services.SettingsStore.Data.StreamChatOverlayEnabled;
                if (chatOverlayEnabled)
                {
                    Status = "Chat overlay hazırlanıyor...";
                    await Task.Delay(1500);  // Pipe server'ın başlaması için bekle
                }

                var result = await _stream.StartWithResultAsync(targets, settings, _cts.Token);

                if (result.Success)
                {
                    IsRunning = true;
                    Status = "Yayında";

                    // Yayın süre sayacını başlat
                    StartStreamTimer();

                    // Toast bildirimi göster
                    Services.ToastService.Instance.ShowSuccess("🎬 Yayın başladı!");

                    // Chat ingestors için event fırlat
                    StreamStarted?.Invoke(targets);

                    // DÜZELTME v27: Task referansını tut (fire-and-forget yerine tracked task)
                    var token = _cts.Token;
                    _statusMonitorTask = Task.Run(async () =>
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
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ControlViewModel] Status monitor exception: {ex.Message}");
                        }
                    }, token);
                }
                else
                {
                    IsRunning = false;
                    Status = "Hata";
                    Advisory = result.UserMessage ?? "Bilinmeyen bir hata oluştu.";
                    
                    // Hata durumunda pipe'ı temizle
                    _preview.StopStreaming();
                    _stream.ClearPipeInput();

                    // Toast bildirimi göster
                    Services.ToastService.Instance.ShowError("Yayın başlatılamadı");
                }
            }
            catch (Exception ex)
            {
                Status = "Kritik Hata";
                Advisory = $"Beklenmedik hata: {ex.Message}";
                IsRunning = false;
                
                // Hata durumunda pipe'ı temizle
                _preview.StopStreaming();
                _stream.ClearPipeInput();
            }
            finally
            {
                IsLoading = false;
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
                catch (Exception ex)
                {
                    // DÜZELTME v26: Boş catch'e loglama eklendi
                    System.Diagnostics.Debug.WriteLine($"[ControlViewModel.StopAsync] CTS cancel hatası: {ex.Message}");
                }

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
                
                // Streaming pipe'ı durdur (preview çalışmaya devam eder)
                _preview.StopStreaming();
                _stream.ClearPipeInput();

                // Yayın süre sayacını durdur
                StopStreamTimer();

                // Toast bildirimi göster
                Services.ToastService.Instance.ShowInfo("⏹ Yayın durduruldu");

                // Chat ingestors için event fırlat
                StreamStopped?.Invoke();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Yayın süre sayacını durdur
            StopStreamTimer();

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

                // DÜZELTME v27: Status monitor task'ın bitmesini bekle
                if (_statusMonitorTask != null)
                {
                    try
                    {
                        _statusMonitorTask.Wait(TimeSpan.FromMilliseconds(500));
                    }
                    catch (AggregateException) { /* Task cancelled, expected */ }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ControlViewModel.Dispose] Status monitor wait exception: {ex.Message}");
                    }
                }
                _statusMonitorTask = null;

                _cts?.Dispose();
            }
            catch (Exception ex)
            {
                // DÜZELTME v26: Boş catch'e loglama eklendi
                System.Diagnostics.Debug.WriteLine($"[ControlViewModel.Dispose] CTS temizleme hatası: {ex.Message}");
            }
            _cts = null;

            // PropertyChanged'i temizle
            PropertyChanged = null;
        }
    }
}