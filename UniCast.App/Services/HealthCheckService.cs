using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using UniCast.Core.Chat;
using Timer = System.Threading.Timer;

namespace UniCast.App.Services
{
    /// <summary>
    /// DÜZELTME v19: Sistem sağlık kontrolü servisi
    /// CPU, RAM, Disk, Ağ ve platform bağlantılarını izler
    /// </summary>
    public sealed class HealthCheckService : IDisposable
    {
        #region Singleton

        private static readonly Lazy<HealthCheckService> _instance =
            new(() => new HealthCheckService(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static HealthCheckService Instance => _instance.Value;

        #endregion

        #region Fields

        private Timer? _checkTimer;
        private readonly HttpClient _httpClient;
        private PerformanceCounter? _cpuCounter;
        private HealthStatus _lastStatus = new();
        private bool _disposed;

        private static class Config
        {
            public const int CheckIntervalSeconds = 30;
            public const double CpuWarningThreshold = 80;
            public const double CpuCriticalThreshold = 95;
            public const double RamWarningThreshold = 85;
            public const double RamCriticalThreshold = 95;
            public const double DiskWarningThresholdGb = 5;
            public const double DiskCriticalThresholdGb = 1;
        }

        #endregion

        #region Events

        /// <summary>
        /// Sağlık durumu değiştiğinde tetiklenir
        /// </summary>
        public event EventHandler<HealthStatusChangedEventArgs>? OnStatusChanged;

        /// <summary>
        /// Kritik sorun algılandığında tetiklenir
        /// </summary>
        public event EventHandler<CriticalIssueEventArgs>? OnCriticalIssue;

        #endregion

        #region Constructor

        private HealthCheckService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue(); // İlk okuma boş döner
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[HealthCheck] CPU counter oluşturulamadı");
            }
        }

        #endregion

        #region Start/Stop

        /// <summary>
        /// Sağlık kontrolünü başlat
        /// </summary>
        public void Start()
        {
            if (_checkTimer != null) return;

            _checkTimer = new Timer(
                async _ => await CheckHealthAsync(),
                null,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(Config.CheckIntervalSeconds));

            Log.Information("[HealthCheck] Servis başlatıldı");
        }

        /// <summary>
        /// Sağlık kontrolünü durdur
        /// </summary>
        public void Stop()
        {
            _checkTimer?.Dispose();
            _checkTimer = null;
            Log.Information("[HealthCheck] Servis durduruldu");
        }

        #endregion

        #region Health Check

        /// <summary>
        /// Anlık sağlık kontrolü yap
        /// </summary>
        public async Task<HealthStatus> CheckHealthAsync()
        {
            var status = new HealthStatus
            {
                Timestamp = DateTime.UtcNow
            };

            try
            {
                // Paralel kontroller
                var tasks = new List<Task>
                {
                    Task.Run(() => CheckCpu(status)),
                    Task.Run(() => CheckMemory(status)),
                    Task.Run(() => CheckDisk(status)),
                    CheckNetworkAsync(status),
                    CheckPlatformEndpointsAsync(status)
                };

                await Task.WhenAll(tasks);

                // Genel durum hesapla
                status.OverallStatus = CalculateOverallStatus(status);

                // Değişiklik kontrolü
                if (HasStatusChanged(status))
                {
                    var oldStatus = _lastStatus;
                    _lastStatus = status;

                    OnStatusChanged?.Invoke(this, new HealthStatusChangedEventArgs
                    {
                        OldStatus = oldStatus,
                        NewStatus = status
                    });

                    // Kritik sorun kontrolü
                    CheckForCriticalIssues(status);
                }

                Log.Debug("[HealthCheck] Durum: {Status} (CPU: {Cpu}%, RAM: {Ram}%)",
                    status.OverallStatus, status.CpuUsage, status.MemoryUsage);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[HealthCheck] Kontrol hatası");
                status.OverallStatus = HealthLevel.Unknown;
            }

            return status;
        }

        /// <summary>
        /// Son sağlık durumunu al
        /// </summary>
        public HealthStatus GetLastStatus() => _lastStatus;

        #endregion

        #region Individual Checks

        private void CheckCpu(HealthStatus status)
        {
            try
            {
                status.CpuUsage = _cpuCounter?.NextValue() ?? 0;

                status.CpuStatus = status.CpuUsage switch
                {
                    >= Config.CpuCriticalThreshold => HealthLevel.Critical,
                    >= Config.CpuWarningThreshold => HealthLevel.Warning,
                    _ => HealthLevel.Healthy
                };
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[HealthCheck] CPU kontrolü hatası");
                status.CpuStatus = HealthLevel.Unknown;
            }
        }

        private void CheckMemory(HealthStatus status)
        {
            try
            {
                var info = new Microsoft.VisualBasic.Devices.ComputerInfo();
                var totalMb = info.TotalPhysicalMemory / (1024.0 * 1024);
                var availableMb = info.AvailablePhysicalMemory / (1024.0 * 1024);
                var usedMb = totalMb - availableMb;

                status.MemoryUsage = (usedMb / totalMb) * 100;
                status.AvailableMemoryMb = availableMb;

                status.MemoryStatus = status.MemoryUsage switch
                {
                    >= Config.RamCriticalThreshold => HealthLevel.Critical,
                    >= Config.RamWarningThreshold => HealthLevel.Warning,
                    _ => HealthLevel.Healthy
                };
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[HealthCheck] RAM kontrolü hatası");
                status.MemoryStatus = HealthLevel.Unknown;
            }
        }

        private void CheckDisk(HealthStatus status)
        {
            try
            {
                var appDrive = new DriveInfo(Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory) ?? "C:");
                status.AvailableDiskGb = appDrive.AvailableFreeSpace / (1024.0 * 1024 * 1024);

                status.DiskStatus = status.AvailableDiskGb switch
                {
                    <= Config.DiskCriticalThresholdGb => HealthLevel.Critical,
                    <= Config.DiskWarningThresholdGb => HealthLevel.Warning,
                    _ => HealthLevel.Healthy
                };
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[HealthCheck] Disk kontrolü hatası");
                status.DiskStatus = HealthLevel.Unknown;
            }
        }

        private async Task CheckNetworkAsync(HealthStatus status)
        {
            try
            {
                status.IsNetworkAvailable = NetworkInterface.GetIsNetworkAvailable();

                if (status.IsNetworkAvailable)
                {
                    // İnternet bağlantısı kontrolü
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var response = await _httpClient.GetAsync("https://www.google.com/generate_204", cts.Token);
                    status.IsInternetAvailable = response.IsSuccessStatusCode;
                }

                status.NetworkStatus = (status.IsNetworkAvailable, status.IsInternetAvailable) switch
                {
                    (false, _) => HealthLevel.Critical,
                    (true, false) => HealthLevel.Warning,
                    _ => HealthLevel.Healthy
                };
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[HealthCheck] Ağ kontrolü hatası");
                status.NetworkStatus = HealthLevel.Warning;
                status.IsInternetAvailable = false;
            }
        }

        private async Task CheckPlatformEndpointsAsync(HealthStatus status)
        {
            var endpoints = new Dictionary<string, string>
            {
                ["YouTube"] = "https://www.googleapis.com/youtube/v3/",
                ["Twitch"] = "https://api.twitch.tv/helix/",
                ["TikTok"] = "https://www.tiktok.com/",
                ["Instagram"] = "https://www.instagram.com/",
                ["Facebook"] = "https://graph.facebook.com/"
            };

            status.PlatformStatuses = new Dictionary<string, HealthLevel>();

            foreach (var (platform, url) in endpoints)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var response = await _httpClient.GetAsync(url, cts.Token);

                    // 4xx hatalar bile endpoint'in erişilebilir olduğunu gösterir
                    status.PlatformStatuses[platform] = response.StatusCode switch
                    {
                        >= System.Net.HttpStatusCode.OK and < System.Net.HttpStatusCode.InternalServerError => HealthLevel.Healthy,
                        _ => HealthLevel.Warning
                    };
                }
                catch
                {
                    status.PlatformStatuses[platform] = HealthLevel.Warning;
                }
            }
        }

        #endregion

        #region Status Analysis

        private HealthLevel CalculateOverallStatus(HealthStatus status)
        {
            var levels = new[]
            {
                status.CpuStatus,
                status.MemoryStatus,
                status.DiskStatus,
                status.NetworkStatus
            };

            if (levels.Any(l => l == HealthLevel.Critical))
                return HealthLevel.Critical;

            if (levels.Any(l => l == HealthLevel.Warning))
                return HealthLevel.Warning;

            if (levels.All(l => l == HealthLevel.Healthy))
                return HealthLevel.Healthy;

            return HealthLevel.Unknown;
        }

        private bool HasStatusChanged(HealthStatus newStatus)
        {
            return newStatus.OverallStatus != _lastStatus.OverallStatus ||
                   newStatus.CpuStatus != _lastStatus.CpuStatus ||
                   newStatus.MemoryStatus != _lastStatus.MemoryStatus ||
                   newStatus.DiskStatus != _lastStatus.DiskStatus ||
                   newStatus.NetworkStatus != _lastStatus.NetworkStatus;
        }

        private void CheckForCriticalIssues(HealthStatus status)
        {
            var issues = new List<string>();

            if (status.CpuStatus == HealthLevel.Critical)
                issues.Add($"CPU kullanımı kritik: {status.CpuUsage:F0}%");

            if (status.MemoryStatus == HealthLevel.Critical)
                issues.Add($"RAM kullanımı kritik: {status.MemoryUsage:F0}%");

            if (status.DiskStatus == HealthLevel.Critical)
                issues.Add($"Disk alanı kritik: {status.AvailableDiskGb:F1} GB");

            if (status.NetworkStatus == HealthLevel.Critical)
                issues.Add("Ağ bağlantısı yok");

            if (issues.Any())
            {
                OnCriticalIssue?.Invoke(this, new CriticalIssueEventArgs
                {
                    Issues = issues,
                    Status = status
                });
            }
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _cpuCounter?.Dispose();
            _httpClient.Dispose();

            OnStatusChanged = null;
            OnCriticalIssue = null;
        }

        #endregion
    }

    #region Health Types

    public enum HealthLevel
    {
        Unknown,
        Healthy,
        Warning,
        Critical
    }

    public class HealthStatus
    {
        public DateTime Timestamp { get; set; }
        public HealthLevel OverallStatus { get; set; }

        // CPU
        public double CpuUsage { get; set; }
        public HealthLevel CpuStatus { get; set; }

        // Memory
        public double MemoryUsage { get; set; }
        public double AvailableMemoryMb { get; set; }
        public HealthLevel MemoryStatus { get; set; }

        // Disk
        public double AvailableDiskGb { get; set; }
        public HealthLevel DiskStatus { get; set; }

        // Network
        public bool IsNetworkAvailable { get; set; }
        public bool IsInternetAvailable { get; set; }
        public HealthLevel NetworkStatus { get; set; }

        // Platforms
        public Dictionary<string, HealthLevel> PlatformStatuses { get; set; } = new();
    }

    public class HealthStatusChangedEventArgs : EventArgs
    {
        public HealthStatus OldStatus { get; init; } = null!;
        public HealthStatus NewStatus { get; init; } = null!;
    }

    public class CriticalIssueEventArgs : EventArgs
    {
        public List<string> Issues { get; init; } = new();
        public HealthStatus Status { get; init; } = null!;
    }

    #endregion
}
