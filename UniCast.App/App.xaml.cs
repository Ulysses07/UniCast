using System;
using System.IO;
using System.Windows;
using Serilog; // Serilog kütüphanesi
using Application = System.Windows.Application;

namespace UniCast.App
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. Loglama Sistemini Kur (Serilog)
            ConfigureLogging();

            // 2. Global Hata Yakalayıcıları (Crash Kalkanı)
            SetupExceptionHandling();

            Log.Information("===================================================");
            Log.Information($"UniCast Başlatılıyor... Versiyon: {GetType().Assembly.GetName().Version}");
            Log.Information("===================================================");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Information("Uygulama kapatılıyor. Çıkış Kodu: {ExitCode}", e.ApplicationExitCode);
            Log.CloseAndFlush(); // Logları diske yazmayı garantile
            base.OnExit(e);
        }

        private void ConfigureLogging()
        {
            // Log dosyaları "Belgelerim/UniCast/Logs" klasöründe saklanacak
            var logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "UniCast",
                "Logs");

            // Günlük log dosyası: log-20231124.txt formatında
            var logPath = Path.Combine(logFolder, "log-.txt");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug() // Geliştirme aşamasında her şeyi kaydet
                .WriteTo.Debug()      // Visual Studio Output penceresine yaz
                .WriteTo.File(logPath,
                    rollingInterval: RollingInterval.Day, // Her gün yeni dosya
                    retainedFileCountLimit: 7,            // Son 7 günü sakla (Disk dolmasını önle)
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }

        private void SetupExceptionHandling()
        {
            // UI Thread Hataları (WPF Arayüzünden gelenler)
            DispatcherUnhandledException += (s, e) =>
            {
                Log.Fatal(e.Exception, "Kritik UI Hatası (DispatcherUnhandledException)");
                // Kullanıcıya nazik bir mesaj gösterip kapatabiliriz veya devam etmeyi deneyebiliriz
                // e.Handled = true; // Bunu açarsan uygulama kapanmaz ama risklidir.
            };

            // Arka Plan Thread Hataları (Task.Run içindekiler)
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                Log.Fatal(ex, "Kritik Sistem Hatası (AppDomain.UnhandledException) - Uygulama Kapanıyor mu: {IsTerminating}", e.IsTerminating);
            };

            // Kaybolmuş Task Hataları (UnobservedTaskException)
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.Error(e.Exception, "Arka planda yakalanmamış Task hatası");
                e.SetObserved(); // Uygulamanın çökmesini engelle
            };
        }
    }
}