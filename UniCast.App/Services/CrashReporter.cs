using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;
using UniCast.Core.Http;

namespace UniCast.App.Services
{
    /// <summary>
    /// DÜZELTME v24: Çökme raporlama servisi
    /// - HttpClient socket exhaustion düzeltildi (SharedHttpClients)
    /// - Boş catch bloklarına loglama eklendi
    /// </summary>
    public static class CrashReporter
    {
        #region Configuration

        private static class Config
        {
            public const string CrashFolderName = "CrashReports";
            public const string ReportFilePattern = "crash_{0:yyyyMMdd_HHmmss}.json";
            public const int MaxReportsToKeep = 10;
            public const int MaxLogLinesInReport = 500;
            public const string ReportEndpoint = ""; // Opsiyonel: crash raporlama sunucusu
        }

        private static string CrashFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UniCast", Config.CrashFolderName);

        #endregion

        #region Fields

        private static bool _initialized;
        private static readonly object _initLock = new();

        #endregion

        #region Initialization

        /// <summary>
        /// Crash reporter'ı başlat
        /// </summary>
        public static void Initialize()
        {
            lock (_initLock)
            {
                if (_initialized) return;

                // Unhandled exception handler'ları kaydet
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

                // WPF için
                if (System.Windows.Application.Current != null)
                {
                    System.Windows.Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
                }

                // Eski raporları temizle
                CleanupOldReports();

                _initialized = true;
                Log.Information("[CrashReporter] Başlatıldı");
            }
        }

        #endregion

        #region Exception Handlers

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception ?? new Exception("Unknown exception");

            Log.Fatal(exception, "[CrashReporter] Unhandled exception! IsTerminating: {IsTerminating}", e.IsTerminating);

            var report = CreateCrashReport(exception, "UnhandledException", e.IsTerminating);
            SaveReportSync(report);

            if (e.IsTerminating)
            {
                ShowCrashDialog(exception);
            }
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Log.Error(e.Exception, "[CrashReporter] Unobserved task exception");

            var report = CreateCrashReport(e.Exception, "UnobservedTaskException", false);
            _ = SaveReportAsync(report);

            // Task exception'ları uygulamayı kapatmaz, sadece logla
            e.SetObserved();
        }

        private static void OnDispatcherUnhandledException(object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Fatal(e.Exception, "[CrashReporter] Dispatcher unhandled exception");

            var report = CreateCrashReport(e.Exception, "DispatcherUnhandledException", true);
            SaveReportSync(report);

            ShowCrashDialog(e.Exception);

            e.Handled = true; // Uygulamanın kapanmasını engelle
        }

        #endregion

        #region Report Creation

        /// <summary>
        /// Crash raporu oluştur
        /// </summary>
        public static CrashReport CreateCrashReport(Exception exception, string source, bool isFatal)
        {
            var report = new CrashReport
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = DateTime.UtcNow,
                Source = source,
                IsFatal = isFatal,

                // Exception detayları
                ExceptionType = exception.GetType().FullName ?? "Unknown",
                ExceptionMessage = exception.Message,
                StackTrace = exception.StackTrace ?? "",
                InnerException = exception.InnerException?.Message,

                // Sistem bilgileri
                AppVersion = GetAppVersion(),
                OsVersion = Environment.OSVersion.ToString(),
                DotNetVersion = Environment.Version.ToString(),
                Is64Bit = Environment.Is64BitProcess,
                MachineName = Environment.MachineName,
                UserName = Environment.UserName,

                // Process bilgileri
                WorkingSetMb = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024),
                ThreadCount = Process.GetCurrentProcess().Threads.Count,
                Uptime = (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(),

                // Son log satırları
                RecentLogs = GetRecentLogs()
            };

            // AggregateException için inner exception'ları ekle
            if (exception is AggregateException aggEx)
            {
                report.AggregatedExceptions = new List<string>();
                foreach (var inner in aggEx.InnerExceptions)
                {
                    report.AggregatedExceptions.Add($"{inner.GetType().Name}: {inner.Message}");
                }
            }

            return report;
        }

        private static string GetAppVersion()
        {
            try
            {
                return System.Reflection.Assembly.GetExecutingAssembly()
                    .GetName().Version?.ToString() ?? "Unknown";
            }
            catch (Exception ex)
            {
                // DÜZELTME v24: Boş catch yerine debug log
                Debug.WriteLine($"[CrashReporter] GetAppVersion error: {ex.Message}");
                return "Unknown";
            }
        }

        private static List<string> GetRecentLogs()
        {
            var logs = new List<string>();

            try
            {
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UniCast", "Logs");

                if (!Directory.Exists(logPath)) return logs;

                var latestLog = Directory.GetFiles(logPath, "*.log")
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .FirstOrDefault();

                if (latestLog == null) return logs;

                // Son N satırı al
                var allLines = File.ReadAllLines(latestLog);
                var startIndex = Math.Max(0, allLines.Length - Config.MaxLogLinesInReport);

                for (int i = startIndex; i < allLines.Length; i++)
                {
                    logs.Add(allLines[i]);
                }
            }
            catch (Exception ex)
            {
                // DÜZELTME v24: Boş catch yerine debug log
                Debug.WriteLine($"[CrashReporter] GetRecentLogs error: {ex.Message}");
            }

            return logs;
        }

        #endregion

        #region Report Saving

        /// <summary>
        /// Raporu async kaydet
        /// </summary>
        public static async Task SaveReportAsync(CrashReport report)
        {
            try
            {
                EnsureCrashFolder();

                var fileName = string.Format(Config.ReportFilePattern, report.Timestamp);
                var filePath = Path.Combine(CrashFolder, fileName);

                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);

                Log.Information("[CrashReporter] Rapor kaydedildi: {Path}", filePath);

                // Opsiyonel: Sunucuya gönder
                if (!string.IsNullOrEmpty(Config.ReportEndpoint))
                {
                    await SendReportToServerAsync(report).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[CrashReporter] Rapor kaydetme hatası");
            }
        }

        /// <summary>
        /// Raporu sync kaydet (crash anında)
        /// </summary>
        private static void SaveReportSync(CrashReport report)
        {
            try
            {
                EnsureCrashFolder();

                var fileName = string.Format(Config.ReportFilePattern, report.Timestamp);
                var filePath = Path.Combine(CrashFolder, fileName);

                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                // DÜZELTME v24: Crash anında bile debug log yaz
                Debug.WriteLine($"[CrashReporter] SaveReportSync error: {ex.Message}");
            }
        }

        /// <summary>
        /// DÜZELTME v24: SharedHttpClients kullan - socket exhaustion önleme
        /// </summary>
        private static async Task SendReportToServerAsync(CrashReport report)
        {
            try
            {
                // DÜZELTME v24: using var client = new HttpClient yerine SharedHttpClients
                var json = JsonSerializer.Serialize(report);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                await SharedHttpClients.Default.PostAsync(Config.ReportEndpoint, content).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // DÜZELTME v24: Boş catch yerine debug log
                Debug.WriteLine($"[CrashReporter] SendReportToServer error: {ex.Message}");
            }
        }

        #endregion

        #region Crash Dialog

        private static void ShowCrashDialog(Exception exception)
        {
            try
            {
                var message = new StringBuilder();
                message.AppendLine("UniCast beklenmeyen bir hata ile karşılaştı.");
                message.AppendLine();
                message.AppendLine($"Hata: {exception.Message}");
                message.AppendLine();
                message.AppendLine("Crash raporu kaydedildi.");
                message.AppendLine($"Konum: {CrashFolder}");
                message.AppendLine();
                message.AppendLine("Uygulama kapatılacak. Sorun devam ederse lütfen destek ile iletişime geçin.");

                System.Windows.MessageBox.Show(
                    message.ToString(),
                    "UniCast - Kritik Hata",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                // DÜZELTME v24: Boş catch yerine debug log
                Debug.WriteLine($"[CrashReporter] ShowCrashDialog error: {ex.Message}");
            }
        }

        #endregion

        #region Report Management

        /// <summary>
        /// Crash raporlarını listele
        /// </summary>
        public static IEnumerable<CrashReportSummary> GetReportSummaries()
        {
            if (!Directory.Exists(CrashFolder))
                yield break;

            foreach (var file in Directory.GetFiles(CrashFolder, "crash_*.json"))
            {
                CrashReportSummary? summary = null;
                try
                {
                    var json = File.ReadAllText(file);
                    var report = JsonSerializer.Deserialize<CrashReport>(json);

                    if (report != null)
                    {
                        summary = new CrashReportSummary
                        {
                            FilePath = file,
                            Timestamp = report.Timestamp,
                            ExceptionType = report.ExceptionType,
                            ExceptionMessage = report.ExceptionMessage,
                            IsFatal = report.IsFatal
                        };
                    }
                }
                catch (Exception ex)
                {
                    // DÜZELTME v24: Boş catch yerine debug log
                    Debug.WriteLine($"[CrashReporter] GetReportSummaries read error: {ex.Message}");
                }

                if (summary != null)
                    yield return summary;
            }
        }

        /// <summary>
        /// Raporu oku
        /// </summary>
        public static CrashReport? LoadReport(string filePath)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<CrashReport>(json);
            }
            catch (Exception ex)
            {
                // DÜZELTME v24: Boş catch yerine debug log
                Debug.WriteLine($"[CrashReporter] LoadReport error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Tüm raporları temizle
        /// </summary>
        public static void ClearAllReports()
        {
            if (!Directory.Exists(CrashFolder)) return;

            foreach (var file in Directory.GetFiles(CrashFolder, "crash_*.json"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    // DÜZELTME v24: Boş catch yerine debug log
                    Debug.WriteLine($"[CrashReporter] ClearAllReports delete error: {ex.Message}");
                }
            }
        }

        private static void CleanupOldReports()
        {
            if (!Directory.Exists(CrashFolder)) return;

            try
            {
                var reports = Directory.GetFiles(CrashFolder, "crash_*.json")
                    .OrderByDescending(f => File.GetCreationTime(f))
                    .Skip(Config.MaxReportsToKeep);

                foreach (var file in reports)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        // DÜZELTME v24: Boş catch yerine debug log
                        Debug.WriteLine($"[CrashReporter] CleanupOldReports delete error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // DÜZELTME v24: Boş catch yerine debug log
                Debug.WriteLine($"[CrashReporter] CleanupOldReports error: {ex.Message}");
            }
        }

        private static void EnsureCrashFolder()
        {
            if (!Directory.Exists(CrashFolder))
            {
                Directory.CreateDirectory(CrashFolder);
            }
        }

        #endregion
    }

    #region Crash Report Types

    public class CrashReport
    {
        public string Id { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string Source { get; set; } = "";
        public bool IsFatal { get; set; }

        // Exception
        public string ExceptionType { get; set; } = "";
        public string ExceptionMessage { get; set; } = "";
        public string StackTrace { get; set; } = "";
        public string? InnerException { get; set; }
        public List<string>? AggregatedExceptions { get; set; }

        // System
        public string AppVersion { get; set; } = "";
        public string OsVersion { get; set; } = "";
        public string DotNetVersion { get; set; } = "";
        public bool Is64Bit { get; set; }
        public string MachineName { get; set; } = "";
        public string UserName { get; set; } = "";

        // Process
        public double WorkingSetMb { get; set; }
        public int ThreadCount { get; set; }
        public string Uptime { get; set; } = "";

        // Logs
        public List<string> RecentLogs { get; set; } = new();
    }

    public class CrashReportSummary
    {
        public string FilePath { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string ExceptionType { get; set; } = "";
        public string ExceptionMessage { get; set; } = "";
        public bool IsFatal { get; set; }
    }

    #endregion
}