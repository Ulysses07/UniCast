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
    /// DÜZELTME v31: Timeout ve async pattern düzeltmeleri
    /// App partial class - Kapatma işlemleri
    /// </summary>
    public partial class App
    {
        #region Shutdown Configuration

        private static class ShutdownConfig
        {
            public const int TotalTimeoutMs = 10000;      // Toplam max bekleme
            public const int StreamStopTimeoutMs = 5000;  // Stream durdurma timeout
            public const int IngestorStopTimeoutMs = 1000; // DÜZELTME v31: 3000 -> 1000ms
            public const int SaveSettingsTimeoutMs = 2000; // Ayar kaydetme timeout
        }

        // DÜZELTME v25: Thread safety - volatile eklendi
        private static volatile bool _isShuttingDown = false;
        private static readonly object _shutdownLock = new();

        #endregion

        #region Graceful Shutdown

        /// <summary>
        /// DÜZELTME v18: Gelişmiş graceful shutdown
        /// DÜZELTME v29: Professional services cleanup eklendi
        /// DÜZELTME v31: Timeout ve async pattern düzeltmeleri
        /// Bu metod App.xaml.cs OnExit'ten çağrılmalıdır
        /// </summary>
        public void PerformGracefulShutdown()
        {
            lock (_shutdownLock)
            {
                if (_isShuttingDown)
                {
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
                    ("Telemetry Shutdown", ShutdownTelemetryAsync, 2000), // Enterprise: Telemetry verilerini gönder
                    ("Stream Durdurma", StopStreamingAsync, ShutdownConfig.StreamStopTimeoutMs),
                    ("Chat Sistemi", StopChatSystemAsync, ShutdownConfig.IngestorStopTimeoutMs),
                    ("Professional Services", CleanupProfessionalServicesAsync, 2000), // v29
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

                // Crash marker temizle (normal kapatma)
                ClearCrashMarker();

                Log.Information("[App] Graceful Shutdown tamamlandı ({ElapsedMs}ms)", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[App] Shutdown sırasında hata");
            }
        }

        /// <summary>
        /// Shutdown task'ı timeout ile çalıştır
        /// DÜZELTME v31: Task.WaitAny ile doğru async pattern
        /// </summary>
        private void ExecuteShutdownTask(string name, Func<Task> action, int timeoutMs)
        {
            try
            {
                Log.Debug("[App] {TaskName} başlatılıyor...", name);

                var task = action();

                // DÜZELTME v31: Task.WaitAny kullanarak timeout kontrolü
                var completedIndex = Task.WaitAny(new[] { task }, timeoutMs);

                if (completedIndex == -1) // -1 = timeout
                {
                    Log.Warning("[App] {TaskName} timeout ({TimeoutMs}ms)", name, timeoutMs);
                }
                else if (task.IsFaulted)
                {
                    Log.Warning(task.Exception?.InnerException, "[App] {TaskName} hata ile tamamlandı", name);
                }
                else
                {
                    Log.Debug("[App] {TaskName} tamamlandı", name);
                }
            }
            catch (AggregateException ae) when (ae.InnerException is TaskCanceledException or OperationCanceledException)
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
        /// Telemetry servisini kapat ve bekleyen verileri gönder
        /// </summary>
        private async Task ShutdownTelemetryAsync()
        {
            try
            {
                Log.Debug("[Shutdown] Telemetry servisi kapatılıyor...");
                await Services.TelemetryService.Instance.ShutdownAsync().ConfigureAwait(false);
                Services.TelemetryService.Instance.Dispose();
                Services.FeatureFlagService.Instance.Dispose();
                Log.Debug("[Shutdown] Enterprise services disposed");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Shutdown] Telemetry shutdown hatası: {ex.Message}");
            }
        }

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
        /// DÜZELTME v31: Task.Run ile sarmalama ve gereksiz delay kaldırıldı
        /// </summary>
        private Task StopChatSystemAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    // ChatBus'u temizle
                    ChatBus.Instance.ClearSubscribers();
                    // DÜZELTME v31: Task.Delay kaldırıldı - gereksiz bekleme
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[App] Chat sistemi durdurma hatası");
                }
            });
        }

        /// <summary>
        /// Ayarları kaydet
        /// DÜZELTME v31: Task.Run ile senkron Save sarmalandı
        /// </summary>
        private Task SaveSettingsAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    // Cleanup hem timer'ı durdurur hem de kaydeder
                    SettingsStore.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[App] Ayar kaydetme hatası");
                }
            });
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
            catch (Exception ex)
            {
                // DÜZELTME v25: Boş catch'e loglama eklendi
                System.Diagnostics.Debug.WriteLine($"[App.Shutdown] Log flush hatası: {ex.Message}");
            }
        }

        /// <summary>
        /// DÜZELTME v29: Professional services cleanup
        /// Hardware encoder, memory pool, GPU compositor, frame timing temizliği
        /// </summary>
        private Task CleanupProfessionalServicesAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    // Frame Timing Service
                    try
                    {
                        UniCast.Encoder.Timing.FrameTimingService.Instance.Dispose();
                        Log.Debug("[Shutdown] FrameTimingService disposed");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Shutdown] FrameTimingService dispose hatası: {ex.Message}");
                    }

                    // Memory Pool
                    try
                    {
                        var stats = UniCast.Encoder.Memory.FrameBufferPool.Instance.GetStats();
                        Log.Debug("[Shutdown] MemoryPool stats: {Stats}", stats.ToString());
                        UniCast.Encoder.Memory.FrameBufferPool.Instance.Dispose();
                        Log.Debug("[Shutdown] FrameBufferPool disposed");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Shutdown] FrameBufferPool dispose hatası: {ex.Message}");
                    }

                    // Native Memory Pool
                    try
                    {
                        UniCast.Encoder.Memory.NativeMemoryPool.Instance.Dispose();
                        Log.Debug("[Shutdown] NativeMemoryPool disposed");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Shutdown] NativeMemoryPool dispose hatası: {ex.Message}");
                    }

                    // GPU Compositor
                    try
                    {
                        UniCast.Encoder.Compositing.GpuCompositor.Instance.Dispose();
                        Log.Debug("[Shutdown] GpuCompositor disposed");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Shutdown] GpuCompositor dispose hatası: {ex.Message}");
                    }

                    // Hardware Encoder Service
                    try
                    {
                        UniCast.Encoder.Hardware.HardwareEncoderService.Instance.Dispose();
                        Log.Debug("[Shutdown] HardwareEncoderService disposed");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Shutdown] HardwareEncoderService dispose hatası: {ex.Message}");
                    }

                    Log.Information("[Shutdown] Professional services cleanup tamamlandı");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Shutdown] Professional services cleanup hatası");
                }
            });
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
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[EmergencyShutdown] Stream stop hatası: {ex.Message}"); }

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
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[EmergencyShutdown] FFmpeg kill hatası: {ex.Message}"); }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[EmergencyShutdown] FFmpeg enumeration hatası: {ex.Message}"); }

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
            catch (Exception ex)
            {
                // DÜZELTME v25: Boş catch'e loglama eklendi
                System.Diagnostics.Debug.WriteLine($"[App.Shutdown] Crash marker temizleme hatası: {ex.Message}");
            }
        }

        #endregion
    }
}
