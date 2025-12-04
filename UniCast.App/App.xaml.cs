using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using UniCast.App.Infrastructure;
using UniCast.App.Input;
using UniCast.App.Logging;
using UniCast.App.Security;
using UniCast.App.Views;
using UniCast.Licensing;
using UniCast.Licensing.Models;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

// DÜZELTME v24: Namespace çakışmalarını önlemek için alias
using DiagnosticsHealthCheck = UniCast.App.Diagnostics.HealthCheckService;
using DiagnosticsMemoryProfiler = UniCast.App.Diagnostics.MemoryProfiler;
using DiagnosticsPerformanceMonitor = UniCast.App.Diagnostics.PerformanceMonitor;
using ConfigValidator = UniCast.App.Configuration.ConfigurationValidator;

namespace UniCast.App
{
    public partial class App : Application
    {
        // DÜZELTME v24: Startup timing
        private Stopwatch? _startupStopwatch;

        // DÜZELTME v24: Event handler referansları (memory leak önleme)
        private EventHandler<Diagnostics.MemoryWarningEventArgs>? _memoryWarningHandler;
        private EventHandler<Diagnostics.HealthStatusChangedEventArgs>? _healthStatusHandler;
        private EventHandler<Diagnostics.PerformanceAlertEventArgs>? _performanceAlertHandler;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // DÜZELTME v24: Startup timing başlat
            _startupStopwatch = Stopwatch.StartNew();

            // 1. Loglama (DynamicLogLevel ile)
            ConfigureLogging();

            // 2. Hata Yakalama
            SetupExceptionHandling();

            Log.Information("===================================================");
            Log.Information($"UniCast Başlatılıyor... Versiyon: {GetType().Assembly.GetName().Version}");
            Log.Information("===================================================");

            // DÜZELTME v17.1: Önceki oturumdan kalan orphan FFmpeg process'leri temizle
            CleanupOrphanFfmpegProcesses();

            // DÜZELTME v24: Configuration Validation (kritik ayarları kontrol et)
            try
            {
                var configResult = ConfigValidator.Instance.Validate();
                foreach (var error in configResult.Errors)
                {
                    if (error.IsCritical)
                    {
                        Log.Fatal("[Config] Kritik hata: {Error}", error.Message);
                        MessageBox.Show(
                            $"Yapılandırma hatası:\n\n{error.Message}\n\nÖneri: {error.Suggestion}",
                            "UniCast - Yapılandırma Hatası",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        Shutdown(1);
                        return;
                    }
                    Log.Warning("[Config] Uyarı: {Error}", error.Message);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Config] Yapılandırma doğrulama hatası (devam ediliyor)");
            }

            // 3. Splash Screen
            SplashWindow? splash = null;
            try
            {
                splash = new SplashWindow();
                splash.Show();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Splash ekranı açılamadı.");
            }

            try
            {
                // 4. LİSANS KONTROLÜ
                var licenseResult = await InitializeLicenseAsync().ConfigureAwait(true);

                if (!licenseResult.IsValid)
                {
                    splash?.Close();
                    await HandleLicenseFailureAsync(licenseResult).ConfigureAwait(true);
                    return;
                }

                // Lisans olaylarını dinle
                LicenseManager.Instance.StatusChanged += OnLicenseStatusChanged;

                Log.Information("Lisans doğrulandı. Tür: {LicenseType}", licenseResult.License?.Type);

                // 5. Ana Pencereyi Aç
                try
                {
                    Log.Debug("MainWindow oluşturuluyor...");
                    var mainWindow = new MainWindow();

                    // KRİTİK: MainWindow'u Application.MainWindow olarak ayarla
                    this.MainWindow = mainWindow;

                    // KRİTİK: MainWindow kapandığında uygulamayı kapat
                    mainWindow.Closed += (s, args) =>
                    {
                        Log.Debug("MainWindow kapandı, uygulama kapatılıyor...");
                        Shutdown();
                    };

                    Log.Debug("MainWindow.Show() çağrılıyor...");
                    mainWindow.Show();

                    // KRİTİK: Splash'ı MainWindow'dan SONRA kapat!
                    splash?.Close();

                    // DÜZELTME v24: Keyboard shortcuts başlat
                    try
                    {
                        KeyboardShortcutManager.Instance.Initialize(mainWindow);
                        Log.Debug("[Shortcuts] Klavye kısayolları başlatıldı");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[Shortcuts] Klavye kısayolları başlatılamadı");
                    }

                    // DÜZELTME v24: Diagnostics servisleri başlat (arka planda)
                    _ = StartDiagnosticsAsync();

                    // DÜZELTME v24: Startup timing bitir
                    _startupStopwatch?.Stop();
                    Log.Information("[Startup] Toplam süre: {TotalMs}ms", _startupStopwatch?.ElapsedMilliseconds);

                    Log.Information("MainWindow başarıyla açıldı");
                }
                catch (Exception ex)
                {
                    Log.Fatal(ex, "MainWindow açılamadı: {Message}", ex.Message);
                    MessageBox.Show(
                        $"Ana pencere açılamadı:\n\n{ex.Message}\n\n{ex.StackTrace}",
                        "UniCast - Kritik Hata",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown(1);
                }
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Uygulama başlatma hatası: {Message}", ex.Message);
                splash?.Close();

                MessageBox.Show(
                    $"Uygulama başlatılamadı:\n\n{ex.Message}",
                    "UniCast - Kritik Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        /// <summary>
        /// DÜZELTME v24: Diagnostics servislerini arka planda başlat
        /// DÜZELTME v29: Professional features (hardware encoder, memory pool) eklendi
        /// - Lambda yerine named handler'lar kullanıldı (memory leak önleme)
        /// </summary>
        private async Task StartDiagnosticsAsync()
        {
            try
            {
                await Task.Run(async () =>
                {
                    try
                    {
                        // DÜZELTME v29: Hardware encoder detection (arka planda)
                        try
                        {
                            var encoders = await UniCast.Encoder.Hardware.HardwareEncoderService.Instance.DetectEncodersAsync();
                            if (encoders.Count > 0)
                            {
                                var best = UniCast.Encoder.Hardware.HardwareEncoderService.Instance.BestEncoder;
                                Log.Information("[HardwareEncoder] Tespit edildi: {Name} ({Count} encoder mevcut)",
                                    best?.Name ?? "Unknown", encoders.Count);
                            }
                            else
                            {
                                Log.Information("[HardwareEncoder] Hardware encoder bulunamadı, software encoding kullanılacak");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "[HardwareEncoder] Detection hatası");
                        }

                        // DÜZELTME v29: Memory pool pre-allocation
                        try
                        {
                            UniCast.Encoder.Memory.FrameBufferPool.Instance.PreAllocate(
                                UniCast.Encoder.Memory.FrameBufferPool.FrameSizes.Size1080p, 4);
                            Log.Debug("[MemoryPool] 1080p buffer'lar pre-allocate edildi");
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "[MemoryPool] Pre-allocation hatası");
                        }

                        // DÜZELTME v29: GPU Compositor check
                        try
                        {
                            if (UniCast.Encoder.Compositing.GpuCompositor.Instance.IsAvailable)
                            {
                                Log.Information("[GpuCompositor] GPU: {Gpu}, DirectX {Level}",
                                    UniCast.Encoder.Compositing.GpuCompositor.Instance.GpuName,
                                    UniCast.Encoder.Compositing.GpuCompositor.Instance.FeatureLevel);
                            }
                            else
                            {
                                Log.Debug("[GpuCompositor] GPU compositing kullanılamıyor, CPU fallback aktif");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "[GpuCompositor] Initialization check hatası");
                        }

                        // DÜZELTME v24: Named handler'lar oluştur
                        _memoryWarningHandler = OnMemoryWarning;
                        _healthStatusHandler = OnHealthStatusChanged;
                        _performanceAlertHandler = OnPerformanceAlert;

                        // Memory Profiler
                        DiagnosticsMemoryProfiler.Instance.StartMonitoring();
                        DiagnosticsMemoryProfiler.Instance.OnMemoryWarning += _memoryWarningHandler;

                        // Health Check
                        DiagnosticsHealthCheck.Instance.Start();
                        DiagnosticsHealthCheck.Instance.OnStatusChanged += _healthStatusHandler;

                        // Performance Monitor
                        DiagnosticsPerformanceMonitor.Instance.StartMonitoring();
                        DiagnosticsPerformanceMonitor.Instance.OnPerformanceAlert += _performanceAlertHandler;

                        Log.Information("[Diagnostics] Tüm servisler başlatıldı");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[Diagnostics] Servis başlatma hatası");
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Diagnostics] Arka plan başlatma hatası");
            }
        }

        // DÜZELTME v24: Named event handlers
        private void OnMemoryWarning(object? sender, Diagnostics.MemoryWarningEventArgs e)
        {
            Log.Warning("[Memory] Uyarı: {Message}", e.Message);
        }

        private void OnHealthStatusChanged(object? sender, Diagnostics.HealthStatusChangedEventArgs e)
        {
            if (e.NewStatus == Diagnostics.HealthStatus.Unhealthy)
            {
                Log.Warning("[Health] Sağlık durumu: {Status}", e.NewStatus);
            }
        }

        private void OnPerformanceAlert(object? sender, Diagnostics.PerformanceAlertEventArgs e)
        {
            if (e.Level == Diagnostics.AlertLevel.Critical)
            {
                Log.Error("[Performance] Kritik: {Type} - {Message}", e.Type, e.Message);
            }
        }

        private async Task<LicenseValidationResult> InitializeLicenseAsync()
        {
            try
            {
#if DEBUG
                Log.Warning("DEBUG modu: Güvenlik kontrolleri atlanıyor...");

                // DÜZELTME v24: ConfigureAwait(false) eklendi
                var result = await Task.Run(() =>
                {
                    try
                    {
                        return LicenseManager.Instance.ValidateAsync().GetAwaiter().GetResult();
                    }
                    catch
                    {
                        return LicenseValidationResult.Failure(LicenseStatus.NotFound, "Lisans bulunamadı");
                    }
                }).ConfigureAwait(false);
#else
                var result = await LicenseManager.Instance.InitializeAsync().ConfigureAwait(false);
#endif

                if (result.Status == LicenseStatus.NotFound)
                {
                    // UI thread'e dön
                    var choice = await Dispatcher.InvokeAsync(() =>
                        MessageBox.Show(
                            "Lisans bulunamadı. Deneme sürümünü başlatmak ister misiniz?\n\n" +
                            "• Evet: 14 günlük ücretsiz deneme başlar\n" +
                            "• Hayır: Lisans anahtarı girişi yaparsınız",
                            "UniCast - Lisans",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question));

                    if (choice == MessageBoxResult.Yes)
                    {
                        result = LicenseManager.Instance.StartTrial();
                        if (result.IsValid)
                        {
                            await Dispatcher.InvokeAsync(() =>
                                MessageBox.Show(
                                    $"14 günlük deneme sürümü başlatıldı!\n\nKalan süre: {result.License?.DaysRemaining} gün",
                                    "UniCast - Deneme Sürümü",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Information));
                        }
                    }
                    else
                    {
                        var activationResult = await Dispatcher.InvokeAsync(() =>
                        {
                            var activationWindow = new ActivationWindow();
                            return activationWindow.ShowDialog() == true;
                        });

                        if (activationResult)
                        {
                            result = await LicenseManager.Instance.ValidateAsync().ConfigureAwait(false);
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Lisans başlatma hatası");

#if DEBUG
                Log.Warning("DEBUG: Lisans hatası yutuldu, trial başlatılıyor...");
                return LicenseManager.Instance.StartTrial();
#else
                return LicenseValidationResult.Failure(LicenseStatus.Tampered, $"Lisans başlatma hatası: {ex.Message}");
#endif
            }
        }

        private async Task HandleLicenseFailureAsync(LicenseValidationResult result)
        {
            string message = result.Status switch
            {
                LicenseStatus.Expired => "Lisans süreniz dolmuş.\n\nYenilemek için satın alma sayfasını ziyaret edin.",
                LicenseStatus.HardwareMismatch => "Bu lisans farklı bir bilgisayarda kullanılıyor.",
                LicenseStatus.InvalidSignature => "Lisans doğrulanamadı.\n\nLütfen destek ile iletişime geçin.",
                LicenseStatus.Revoked => "Bu lisans iptal edilmiş.",
                LicenseStatus.Tampered => "Güvenlik ihlali tespit edildi.\n\nUygulama kapatılıyor.",
                LicenseStatus.MachineLimitExceeded => "Maksimum makine sayısına ulaşıldı.",
                _ => result.Message
            };

            Log.Warning("Lisans hatası: {Status} - {Message}", result.Status, result.Message);

            await Dispatcher.InvokeAsync(() =>
                MessageBox.Show(message, "UniCast - Lisans Hatası", MessageBoxButton.OK, MessageBoxImage.Error));

            if (result.Status == LicenseStatus.Tampered)
            {
#if !DEBUG
                Shutdown(1);
                return;
#else
                Log.Warning("DEBUG: Tampered hatası yutuldu");
#endif
            }

            var activationResult = await Dispatcher.InvokeAsync(() =>
            {
                var activationWindow = new ActivationWindow();
                return activationWindow.ShowDialog() == true;
            });

            if (activationResult)
            {
                var newResult = await LicenseManager.Instance.ValidateAsync().ConfigureAwait(false);
                if (newResult.IsValid)
                {
                    LicenseManager.Instance.StatusChanged += OnLicenseStatusChanged;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        var mainWindow = new MainWindow();
                        this.MainWindow = mainWindow;
                        mainWindow.Closed += (s, args) => Shutdown();
                        mainWindow.Show();
                    });
                    return;
                }
            }

            Shutdown(0);
        }

        private void OnLicenseStatusChanged(object? sender, LicenseStatusChangedEventArgs e)
        {
            Current.Dispatcher.Invoke(() =>
            {
                switch (e.NewStatus)
                {
                    case LicenseStatus.Expired:
                        MessageBox.Show("Lisans süreniz doldu!", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                        break;

                    case LicenseStatus.GracePeriod:
                        var info = LicenseManager.Instance.GetLicenseInfo();
                        MessageBox.Show($"Çevrimdışı mod aktif.\n\n{info.DaysRemaining} gün kaldı.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                        break;

                    case LicenseStatus.Tampered:
#if !DEBUG
                        MessageBox.Show("Güvenlik ihlali!", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                        Shutdown(1);
#endif
                        break;
                }
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Information("Uygulama kapatılıyor. Çıkış Kodu: {ExitCode}", e.ApplicationExitCode);

            // DÜZELTME v24: Diagnostics servislerini durdur
            StopDiagnostics();

            // DÜZELTME v18: Gelişmiş graceful shutdown
            PerformGracefulShutdown();

            try
            {
                LicenseManager.Instance.StatusChanged -= OnLicenseStatusChanged;
                LicenseManager.Instance.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "LicenseManager dispose hatası");
            }

            Log.CloseAndFlush();
            base.OnExit(e);
        }

        /// <summary>
        /// DÜZELTME v24: Diagnostics servislerini durdur ve event handler'ları temizle
        /// </summary>
        private void StopDiagnostics()
        {
            try
            {
                // DÜZELTME v24: Event handler'ları temizle (memory leak önleme)
                if (_memoryWarningHandler != null)
                {
                    DiagnosticsMemoryProfiler.Instance.OnMemoryWarning -= _memoryWarningHandler;
                    _memoryWarningHandler = null;
                }

                if (_healthStatusHandler != null)
                {
                    DiagnosticsHealthCheck.Instance.OnStatusChanged -= _healthStatusHandler;
                    _healthStatusHandler = null;
                }

                if (_performanceAlertHandler != null)
                {
                    DiagnosticsPerformanceMonitor.Instance.OnPerformanceAlert -= _performanceAlertHandler;
                    _performanceAlertHandler = null;
                }

                DiagnosticsMemoryProfiler.Instance.StopMonitoring();
                DiagnosticsHealthCheck.Instance.Stop();
                DiagnosticsPerformanceMonitor.Instance.StopMonitoring();

                Log.Debug("[Diagnostics] Servisler durduruldu");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Diagnostics] Servis durdurma hatası");
            }
        }

        private void ConfigureLogging()
        {
            var logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                AppConstants.Paths.AppFolderName,
                AppConstants.Paths.LogFolderName);

            var logPath = Path.Combine(logFolder, "log-.txt");

            // DÜZELTME v24: DynamicLogLevel ile runtime log level değiştirme
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(DynamicLogLevel.Instance.LevelSwitch)
                .Enrich.With<StreamKeyMaskingEnricher>()
                .WriteTo.Debug()
                .WriteTo.File(
                    logPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: AppConstants.Limits.LogFileRetainedCount,
                    fileSizeLimitBytes: AppConstants.Limits.LogFileSizeBytes,
                    rollOnFileSizeLimit: true,
                    shared: true)
                .CreateLogger();

            // Debug modunda verbose log
#if DEBUG
            DynamicLogLevel.Instance.EnableDebugMode();
#endif
        }

        private void SetupExceptionHandling()
        {
            DispatcherUnhandledException += (s, e) =>
            {
                Log.Fatal(e.Exception, "Kritik UI Hatası: {Message}", e.Exception.Message);

                // DÜZELTME v24: Crash report kaydet
                try
                {
                    var report = Services.CrashReporter.CreateCrashReport(e.Exception, "UI", false);
                    _ = Services.CrashReporter.SaveReportAsync(report);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CrashReporter] SaveReport error: {ex.Message}");
                }

                MessageBox.Show(
                    $"Bir hata oluştu:\n\n{e.Exception.Message}\n\nDetaylar log dosyasına kaydedildi.",
                    "UniCast - Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                e.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                if (ex != null)
                {
                    Log.Fatal(ex, "Kritik Sistem Hatası: {Message}", ex.Message);

                    try
                    {
                        var report = Services.CrashReporter.CreateCrashReport(ex, "AppDomain", true);
                        _ = Services.CrashReporter.SaveReportAsync(report);
                    }
                    catch (Exception repEx)
                    {
                        Debug.WriteLine($"[CrashReporter] SaveReport error: {repEx.Message}");
                    }

                    MessageBox.Show(
                        $"Kritik bir hata oluştu:\n\n{ex.Message}\n\nUygulama kapatılacak.",
                        "UniCast - Kritik Hata",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            };

            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.Error(e.Exception, "Arka plan Task hatası: {Message}", e.Exception.Message);
                e.SetObserved();
            };
        }

        /// <summary>
        /// DÜZELTME v17.1: Önceki oturumdan kalan orphan FFmpeg process'leri temizler.
        /// </summary>
        private void CleanupOrphanFfmpegProcesses()
        {
            try
            {
                var ffmpegProcesses = System.Diagnostics.Process.GetProcessesByName("ffmpeg");

                if (ffmpegProcesses.Length == 0)
                {
                    Log.Debug("[App] Orphan FFmpeg process bulunamadı");
                    return;
                }

                Log.Warning("[App] {Count} adet orphan FFmpeg process bulundu, temizleniyor...", ffmpegProcesses.Length);

                foreach (var proc in ffmpegProcesses)
                {
                    try
                    {
                        var commandLine = GetProcessCommandLine(proc);

                        bool isUniCastProcess = commandLine?.Contains("UniCast") == true ||
                                                commandLine?.Contains("\\Temp\\") == true ||
                                                proc.StartTime < DateTime.Now.AddHours(-24);

                        if (isUniCastProcess || proc.StartTime < DateTime.Now.AddMinutes(-30))
                        {
                            Log.Information("[App] Orphan FFmpeg process sonlandırılıyor: PID={PID}, StartTime={StartTime}",
                                proc.Id, proc.StartTime);

                            proc.Kill();
                            proc.WaitForExit(2000);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "[App] FFmpeg process sonlandırma hatası: PID={PID}", proc.Id);
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }

                Log.Information("[App] Orphan FFmpeg process temizliği tamamlandı");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[App] Orphan FFmpeg temizliği sırasında hata");
            }
        }

        private string? GetProcessCommandLine(System.Diagnostics.Process process)
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");

                foreach (System.Management.ManagementObject obj in searcher.Get())
                {
                    return obj["CommandLine"]?.ToString();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] GetProcessCommandLine error: {ex.Message}");
            }
            return null;
        }
    }
}