using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using UniCast.App.Views;
using UniCast.Licensing;
using UniCast.Licensing.Models;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace UniCast.App
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. Loglama
            ConfigureLogging();

            // 2. Hata Yakalama
            SetupExceptionHandling();

            Log.Information("===================================================");
            Log.Information($"UniCast Başlatılıyor... Versiyon: {GetType().Assembly.GetName().Version}");
            Log.Information("===================================================");

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
                var licenseResult = await InitializeLicenseAsync();

                // Splash'i kapat
                splash?.Close();

                if (!licenseResult.IsValid)
                {
                    await HandleLicenseFailureAsync(licenseResult);
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

                    Log.Debug("MainWindow.Show() çağrılıyor...");
                    mainWindow.Show();

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

        private async Task<LicenseValidationResult> InitializeLicenseAsync()
        {
            try
            {
#if DEBUG
                Log.Warning("DEBUG modu: Güvenlik kontrolleri atlanıyor...");

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
                });
#else
                var result = await LicenseManager.Instance.InitializeAsync();
#endif

                if (result.Status == LicenseStatus.NotFound)
                {
                    var choice = MessageBox.Show(
                        "Lisans bulunamadı. Deneme sürümünü başlatmak ister misiniz?\n\n" +
                        "• Evet: 14 günlük ücretsiz deneme başlar\n" +
                        "• Hayır: Lisans anahtarı girişi yaparsınız",
                        "UniCast - Lisans",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (choice == MessageBoxResult.Yes)
                    {
                        result = LicenseManager.Instance.StartTrial();
                        if (result.IsValid)
                        {
                            MessageBox.Show(
                                $"14 günlük deneme sürümü başlatıldı!\n\nKalan süre: {result.License?.DaysRemaining} gün",
                                "UniCast - Deneme Sürümü",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        }
                    }
                    else
                    {
                        var activationWindow = new ActivationWindow();
                        if (activationWindow.ShowDialog() == true)
                        {
                            result = await LicenseManager.Instance.ValidateAsync();
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

            MessageBox.Show(message, "UniCast - Lisans Hatası", MessageBoxButton.OK, MessageBoxImage.Error);

            if (result.Status == LicenseStatus.Tampered)
            {
#if !DEBUG
                Shutdown(1);
                return;
#else
                Log.Warning("DEBUG: Tampered hatası yutuldu");
#endif
            }

            var activationWindow = new ActivationWindow();
            if (activationWindow.ShowDialog() == true)
            {
                var newResult = await LicenseManager.Instance.ValidateAsync();
                if (newResult.IsValid)
                {
                    LicenseManager.Instance.StatusChanged += OnLicenseStatusChanged;
                    var mainWindow = new MainWindow();
                    mainWindow.Show();
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

        private void ConfigureLogging()
        {
            var logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "UniCast", "Logs");

            var logPath = Path.Combine(logFolder, "log-.txt");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Debug()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
                .CreateLogger();
        }

        private void SetupExceptionHandling()
        {
            DispatcherUnhandledException += (s, e) =>
            {
                Log.Fatal(e.Exception, "Kritik UI Hatası: {Message}", e.Exception.Message);

                // Hatayı kullanıcıya göster
                MessageBox.Show(
                    $"Bir hata oluştu:\n\n{e.Exception.Message}\n\nDetaylar log dosyasına kaydedildi.",
                    "UniCast - Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                // Uygulamanın kapanmasını önle (kritik olmayan hatalar için)
                e.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                if (ex != null)
                {
                    Log.Fatal(ex, "Kritik Sistem Hatası: {Message}", ex.Message);

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
    }
}