using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using UniCast.App.Views; // SplashWindow için
using Application = System.Windows.Application;

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

            // 3. Splash Screen (Yükleme Ekranı)
            // HATA DÜZELTME: SplashWindow'u try-catch içine aldık ki hata verirse uygulama durmasın
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

            // 4. Sahte Yükleme (Simülasyon) - Burada veritabanı vb. yüklenebilir
            await Task.Delay(1500);

            // 5. Ana Pencereyi Aç
            var mainWindow = new MainWindow();
            mainWindow.Show();

            // Splash'i kapat
            splash?.Close();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Information("Uygulama kapatılıyor. Çıkış Kodu: {ExitCode}", e.ApplicationExitCode);
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
                Log.Fatal(e.Exception, "Kritik UI Hatası");
                // e.Handled = true; // Kapatmak istemiyorsan aç
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                if (ex != null) Log.Fatal(ex, "Kritik Sistem Hatası (Kapanıyor: {IsTerminating})", e.IsTerminating);
            };

            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.Error(e.Exception, "Arka plan Task hatası");
                e.SetObserved();
            };
        }
    }
}