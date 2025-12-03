using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using UniCast.App.Services;
using UniCast.Core.Chat;
using UniCast.Core.Services;

namespace UniCast.App
{
    /// <summary>
    /// DÜZELTME v18: Gelişmiş Graceful Shutdown
    /// App partial class - Kapatma işlemleri
    /// </summary>
    public partial class App
    {
        #region Shutdown Configuration

        private static class ShutdownConfig
        {
            public const int TotalTimeoutMs = 10000;      // Toplam max bekleme
            public const int StreamStopTimeoutMs = 5000;  // Stream durdurma timeout
            public const int IngestorStopTimeoutMs = 3000; // Ingestor durdurma timeout
            public const int SaveSettingsTimeoutMs = 2000; // Ayar kaydetme timeout
        }

        private static bool _isShuttingDown = false;
        private static readonly object _shutdownLock = new();

        #endregion

        #region Graceful Shutdown

        /// <summary>
        /// DÜZELTME v18: Gelişmiş graceful shutdown
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            lock (_shutdownLock)
            {
                if (_isShuttingDown)
                {
                    base.OnExit(e);
                    return;
                }
                _isShuttingDown = true;
            }

            Log.Information("===================================================");
            Log.Information("[App] Uygulama kapatılıyor - Graceful Shutdown başladı");
            Log.Information("===================================================");

            var sw = Stopwatch.StartNew();

            try
            {
                // Tüm shutdown işlemlerini sırayla yap
                var shutdownTasks = new List<(string Name, Func<Task> Action, int TimeoutMs)>
                {
                    ("Stream Durdurma", StopStreamingAsync, ShutdownConfig.StreamStopTimeoutMs),
                    ("Chat Sistemi", StopChatSystemAsync, ShutdownConfig.IngestorStopTimeoutMs),
                    ("Ayarları Kaydet", SaveSettingsAsync, ShutdownConfig.SaveSettingsTimeoutMs),
                    ("Overlay Kapatma", CloseOverlayAsync, 1000),
                    ("Log Flush", FlushLogsAsync, 1000)
                };

                foreach (var (name, action, timeout) in shutdownTasks)
                {
                    if (sw.ElapsedMilliseconds > ShutdownConfig.TotalTimeoutMs)
                    {
                        Log.Warning("[App] Toplam shutdown timeout aşıldı, kalan işlemler atlanıyor");
                        break;
                    }

                    ExecuteShutdownTask(name, action, timeout);
                }

                // FFmpeg orphan temizliği (sync)
                CleanupOrphanFfmpegProcesses();

                Log.Information("[App] Graceful Shutdown tamamlandı ({ElapsedMs}ms)", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[App] Shutdown sırasında hata");
            }
            finally
            {
                // Serilog'u kapat
                Log.CloseAndFlush();
            }

            base.OnExit(e);
        }

        /// <summary>
        /// Shutdown task'ı timeout ile çalıştır
        /// </summary>
        private void ExecuteShutdownTask(string name, Func<Task> action, int timeoutMs)
        {
            try
            {
                Log.Debug("[App] {TaskName} başlatılıyor...", name);

                using var cts = new CancellationTokenSource(timeoutMs);
                var task = action();

                if (!task.Wait(timeoutMs))
                {
                    Log.Warning("[App] {TaskName} timeout ({TimeoutMs}ms)", name, timeoutMs);
                }
                else
                {
                    Log.Debug("[App] {TaskName} tamamlandı", name);
                }
            }
            catch (AggregateException ae) when (ae.InnerException is TaskCanceledException)
            {
                Log.Debug("[App] {TaskName} iptal edildi", name);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[App] {TaskName} hatası", name);
            }
        }

        #endregion

        #region Shutdown Tasks

        /// <summary>
        /// Stream'i durdur
        /// </summary>
        private async Task StopStreamingAsync()
        {
            try
            {
                if (StreamController.Instance.IsRunning)
                {
                    Log.Information("[App] Aktif stream durduruluyor...");
                    StreamController.Instance.Stop();
                    await Task.Delay(500); // Kısa bekleme
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[App] Stream durdurma hatası");
            }
        }

        /// <summary>
        /// Chat sistemini durdur
        /// </summary>
        private async Task StopChatSystemAsync()
        {
            try
            {
                // ChatBus'u temizle
                ChatBus.Instance.ClearSubscribers();
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[App] Chat sistemi durdurma hatası");
            }
        }

        /// <summary>
        /// Ayarları kaydet
        /// </summary>
        private async Task SaveSettingsAsync()
        {
            try
            {
                // Pending değişiklikleri kaydet
                SettingsStore.Save();
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[App] Ayar kaydetme hatası");
            }
        }

        /// <summary>
        /// Overlay'i kapat
        /// </summary>
        private Task CloseOverlayAsync()
        {
            // Overlay MainWindow tarafından yönetiliyor
            return Task.CompletedTask;
        }

        /// <summary>
        /// Log'ları flush et
        /// </summary>
        private async Task FlushLogsAsync()
        {
            try
            {
                await Task.Run(() => Log.CloseAndFlush());
            }
            catch
            {
                // Log flush hatası önemsiz
            }
        }

        #endregion

        #region Emergency Shutdown

        /// <summary>
        /// DÜZELTME v18: Acil kapatma (zorla)
        /// </summary>
        public static void EmergencyShutdown(string reason)
        {
            Log.Fatal("[App] ACİL KAPATMA: {Reason}", reason);

            try
            {
                // Stream'i hemen durdur
                try
                {
                    StreamController.Instance.Stop();
                }
                catch { }

                // FFmpeg'leri öldür
                try
                {
                    foreach (var proc in Process.GetProcessesByName("ffmpeg"))
                    {
                        try
                        {
                            proc.Kill();
                            proc.Dispose();
                        }
                        catch { }
                    }
                }
                catch { }

                // Log flush
                Log.CloseAndFlush();
            }
            finally
            {
                Environment.Exit(1);
            }
        }

        /// <summary>
        /// DÜZELTME v18: Kullanıcıya onay sorarak kapat
        /// </summary>
        public static bool ConfirmShutdown()
        {
            // Stream aktifse uyar
            if (StreamController.Instance.IsRunning)
            {
                var result = System.Windows.MessageBox.Show(
                    "Aktif bir yayın var. Kapatmak istediğinize emin misiniz?\n\nYayın otomatik olarak durdurulacak.",
                    "Yayın Aktif",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                return result == MessageBoxResult.Yes;
            }

            return true;
        }

        #endregion

        #region Crash Recovery

        /// <summary>
        /// DÜZELTME v18: Crash sonrası recovery
        /// Uygulama başlatılırken çağrılır
        /// </summary>
        private void CheckForCrashRecovery()
        {
            try
            {
                var crashMarkerPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UniCast", "crash_marker.tmp");

                if (System.IO.File.Exists(crashMarkerPath))
                {
                    Log.Warning("[App] Önceki oturumda crash algılandı!");

                    // Crash marker'ı sil
                    System.IO.File.Delete(crashMarkerPath);

                    // Recovery işlemleri
                    CleanupOrphanFfmpegProcesses();

                    // Kullanıcıyı bilgilendir
                    System.Windows.MessageBox.Show(
                        "UniCast önceki oturumda beklenmedik şekilde kapanmış.\n\n" +
                        "Orphan process'ler temizlendi. Sorun devam ederse lütfen destek ile iletişime geçin.",
                        "Crash Recovery",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                // Yeni crash marker oluştur (normal kapatmada silinecek)
                var dir = System.IO.Path.GetDirectoryName(crashMarkerPath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }
                System.IO.File.WriteAllText(crashMarkerPath, DateTime.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[App] Crash recovery kontrolü hatası");
            }
        }

        /// <summary>
        /// Crash marker'ı temizle (normal kapatma)
        /// </summary>
        private void ClearCrashMarker()
        {
            try
            {
                var crashMarkerPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UniCast", "crash_marker.tmp");

                if (System.IO.File.Exists(crashMarkerPath))
                {
                    System.IO.File.Delete(crashMarkerPath);
                }
            }
            catch
            {
                // Önemsiz
            }
        }

        #endregion
    }
}
