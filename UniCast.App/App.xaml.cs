using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using UniCast.App.Infrastructure;
using UniCast.App.Services;
using UniCast.App.Services.Capture;
using UniCast.App.Services.Chat;
using UniCast.App.ViewModels;
using UniCast.Core.Chat;
using Application = System.Windows.Application;

namespace UniCast.App
{
    public partial class App : Application
    {
        /// <summary>
        /// DI Container - Tüm servislere buradan erişilir
        /// </summary>
        public static IServiceProvider Services { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. Loglama Sistemini Kur (Serilog)
            ConfigureLogging();

            // 2. Global Hata Yakalayıcıları (Crash Kalkanı)
            SetupExceptionHandling();

            // 3. DI Container Kur
            ConfigureServices();

            Log.Information("===================================================");
            Log.Information($"UniCast Başlatılıyor... Versiyon: {GetType().Assembly.GetName().Version}");
            Log.Information("===================================================");

            // 4. Ana Pencereyi Aç (DI ile)
            var mainWindow = new MainWindow();
            mainWindow.Show();
        }

        /// <summary>
        /// Dependency Injection Container yapılandırması
        /// </summary>
        private void ConfigureServices()
        {
            var services = new ServiceCollection();

            // UniCast servislerini kaydet
            services.AddUniCastServices();

            // Container'ı oluştur
            Services = services.BuildServiceProvider();

            Log.Information("DI Container başlatıldı.");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Information("Uygulama kapatılıyor. Çıkış Kodu: {ExitCode}", e.ApplicationExitCode);

            // DÜZELTME: DI Container'ı async dispose et
            try
            {
                if (Services is IAsyncDisposable asyncDisposable)
                {
                    asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                else if (Services is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "DI Container dispose hatası");
            }

            // DÜZELTME: SettingsStore static lock'u temizle
            try
            {
                SettingsStore.Cleanup();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SettingsStore cleanup hatası");
            }

            Log.CloseAndFlush();
            base.OnExit(e);
        }

        private void ConfigureLogging()
        {
            var logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "UniCast",
                "Logs");

            var logPath = Path.Combine(logFolder, "log-.txt");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Debug()
                .WriteTo.File(logPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }

        private void SetupExceptionHandling()
        {
            // UI Thread Hataları
            DispatcherUnhandledException += (s, e) =>
            {
                Log.Fatal(e.Exception, "Kritik UI Hatası (DispatcherUnhandledException)");
                // e.Handled = true; // Uygulamayı canlı tutmak için
            };

            // Arka Plan Thread Hataları
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                Log.Fatal(ex, "Kritik Sistem Hatası (AppDomain.UnhandledException) - Kapanıyor mu: {IsTerminating}", e.IsTerminating);
            };

            // Kaybolmuş Task Hataları
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.Error(e.Exception, "Arka planda yakalanmamış Task hatası");
                e.SetObserved();
            };
        }
    }
}