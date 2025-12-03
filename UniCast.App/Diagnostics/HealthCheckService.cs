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
using Timer = System.Threading.Timer;

namespace UniCast.App.Diagnostics
{
    /// <summary>
    /// DÜZELTME v24: Health Check servisi
    /// - async void kaldırıldı
    /// - HttpClient SocketsHttpHandler ile düzeltildi
    /// - Magic numbers AppConstants'a taşındı
    /// </summary>
    public sealed class HealthCheckService : IDisposable
    {
        #region Singleton

        private static readonly Lazy<HealthCheckService> _instance =
            new(() => new HealthCheckService(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static HealthCheckService Instance => _instance.Value;

        #endregion

        #region Fields

        private readonly Timer _checkTimer;
        private readonly HttpClient _httpClient;
        private readonly List<IHealthCheck> _checks = new();
        private HealthStatus _lastStatus = HealthStatus.Unknown;
        private volatile bool _isRunning;
        private bool _disposed;

        #endregion

        #region Events

        public event EventHandler<HealthStatusChangedEventArgs>? OnStatusChanged;
        public event EventHandler<HealthCheckFailedEventArgs>? OnCheckFailed;

        #endregion

        #region Constructor

        private HealthCheckService()
        {
            // DÜZELTME v24: SocketsHttpHandler ile proper HttpClient
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(AppConstants.HttpClient.PooledConnectionLifetimeMinutes),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(AppConstants.HttpClient.PooledConnectionIdleTimeoutMinutes),
                MaxConnectionsPerServer = AppConstants.HttpClient.MaxConnectionsPerServer,
                ConnectTimeout = TimeSpan.FromSeconds(AppConstants.HttpClient.ConnectTimeoutSeconds)
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(AppConstants.Timeouts.HealthCheckMs)
            };

            _checkTimer = new Timer(RunChecksCallback, null, Timeout.Infinite, Timeout.Infinite);

            // Varsayılan kontrolleri kaydet
            RegisterDefaultChecks();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Health check'leri başlat
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;

            _isRunning = true;
            _checkTimer.Change(0, AppConstants.Intervals.HealthCheckSeconds * 1000);

            Log.Information("[HealthCheck] Başlatıldı");
        }

        /// <summary>
        /// Health check'leri durdur
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _checkTimer.Change(Timeout.Infinite, Timeout.Infinite);

            Log.Information("[HealthCheck] Durduruldu");
        }

        /// <summary>
        /// Özel health check ekle
        /// </summary>
        public void RegisterCheck(IHealthCheck check)
        {
            _checks.Add(check);
            Log.Debug("[HealthCheck] Yeni kontrol eklendi: {Name}", check.Name);
        }

        /// <summary>
        /// Tüm kontrolleri çalıştır ve sonuç al
        /// </summary>
        public async Task<HealthReport> CheckAllAsync(CancellationToken ct = default)
        {
            var results = new List<HealthCheckResult>();
            var sw = Stopwatch.StartNew();

            foreach (var check in _checks)
            {
                try
                {
                    var result = await check.CheckAsync(ct).ConfigureAwait(false);
                    results.Add(result);

                    if (result.Status == HealthStatus.Unhealthy)
                    {
                        OnCheckFailed?.Invoke(this, new HealthCheckFailedEventArgs
                        {
                            CheckName = check.Name,
                            Result = result
                        });
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[HealthCheck] Check failed: {Name}", check.Name);
                    results.Add(new HealthCheckResult
                    {
                        Name = check.Name,
                        Status = HealthStatus.Unhealthy,
                        Description = $"Check failed: {ex.Message}",
                        Duration = TimeSpan.Zero
                    });
                }
            }

            sw.Stop();

            var overallStatus = DetermineOverallStatus(results);
            var report = new HealthReport
            {
                Timestamp = DateTime.UtcNow,
                Status = overallStatus,
                TotalDuration = sw.Elapsed,
                Results = results
            };

            // Status değiştiyse event tetikle
            if (overallStatus != _lastStatus)
            {
                var oldStatus = _lastStatus;
                _lastStatus = overallStatus;

                OnStatusChanged?.Invoke(this, new HealthStatusChangedEventArgs
                {
                    OldStatus = oldStatus,
                    NewStatus = overallStatus,
                    Report = report
                });
            }

            return report;
        }

        /// <summary>
        /// Son health status'u al
        /// </summary>
        public HealthStatus GetLastStatus() => _lastStatus;

        #endregion

        #region Private Methods

        private void RegisterDefaultChecks()
        {
            _checks.Add(new DiskSpaceCheck());
            _checks.Add(new MemoryCheck());
            _checks.Add(new FFmpegCheck());
            _checks.Add(new NetworkCheck());
            _checks.Add(new LicenseServerCheck(_httpClient));
        }

        /// <summary>
        /// DÜZELTME v24: async void kaldırıldı - Timer callback sync, içeride Task başlatılıyor
        /// </summary>
        private void RunChecksCallback(object? state)
        {
            if (_disposed) return;

            // DÜZELTME v24: Task.Run ile async işlemi başlat, exception'ı logla
            _ = Task.Run(async () =>
            {
                try
                {
                    await CheckAllAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[HealthCheck] Kontrol döngüsü hatası");
                }
            });
        }

        private HealthStatus DetermineOverallStatus(List<HealthCheckResult> results)
        {
            if (results.Any(r => r.Status == HealthStatus.Unhealthy))
                return HealthStatus.Unhealthy;

            if (results.Any(r => r.Status == HealthStatus.Degraded))
                return HealthStatus.Degraded;

            return HealthStatus.Healthy;
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _checkTimer.Dispose();
            _httpClient.Dispose();
            _checks.Clear();

            OnStatusChanged = null;
            OnCheckFailed = null;
        }

        #endregion
    }

    #region Health Check Interface & Types

    public interface IHealthCheck
    {
        string Name { get; }
        Task<HealthCheckResult> CheckAsync(CancellationToken ct = default);
    }

    public enum HealthStatus
    {
        Unknown,
        Healthy,
        Degraded,
        Unhealthy
    }

    public class HealthCheckResult
    {
        public string Name { get; init; } = "";
        public HealthStatus Status { get; init; }
        public string Description { get; init; } = "";
        public TimeSpan Duration { get; init; }
        public Dictionary<string, object>? Data { get; init; }
    }

    public class HealthReport
    {
        public DateTime Timestamp { get; init; }
        public HealthStatus Status { get; init; }
        public TimeSpan TotalDuration { get; init; }
        public List<HealthCheckResult> Results { get; init; } = new();

        public string GetSummary()
        {
            return $"[{Timestamp:HH:mm:ss}] Status: {Status}, Checks: {Results.Count}, Duration: {TotalDuration.TotalMilliseconds:F0}ms";
        }
    }

    public class HealthStatusChangedEventArgs : EventArgs
    {
        public HealthStatus OldStatus { get; init; }
        public HealthStatus NewStatus { get; init; }
        public HealthReport? Report { get; init; }
    }

    public class HealthCheckFailedEventArgs : EventArgs
    {
        public string CheckName { get; init; } = "";
        public HealthCheckResult? Result { get; init; }
    }

    #endregion

    #region Built-in Health Checks

    /// <summary>
    /// Disk alanı kontrolü
    /// </summary>
    public class DiskSpaceCheck : IHealthCheck
    {
        public string Name => "DiskSpace";

        public Task<HealthCheckResult> CheckAsync(CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
                var freeGB = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);

                sw.Stop();

                var status = freeGB < AppConstants.Limits.DiskWarningGB ? HealthStatus.Unhealthy :
                             freeGB < AppConstants.Limits.DiskWarningGB * 2 ? HealthStatus.Degraded :
                             HealthStatus.Healthy;

                return Task.FromResult(new HealthCheckResult
                {
                    Name = Name,
                    Status = status,
                    Description = $"{freeGB:F1} GB boş alan ({drive.Name})",
                    Duration = sw.Elapsed,
                    Data = new Dictionary<string, object>
                    {
                        ["FreeSpaceGB"] = freeGB,
                        ["DriveName"] = drive.Name,
                        ["TotalSizeGB"] = drive.TotalSize / (1024.0 * 1024 * 1024)
                    }
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new HealthCheckResult
                {
                    Name = Name,
                    Status = HealthStatus.Unhealthy,
                    Description = ex.Message,
                    Duration = sw.Elapsed
                });
            }
        }
    }

    /// <summary>
    /// Memory kullanım kontrolü
    /// </summary>
    public class MemoryCheck : IHealthCheck
    {
        public string Name => "Memory";

        public Task<HealthCheckResult> CheckAsync(CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                var managedMB = GC.GetTotalMemory(false) / (1024.0 * 1024);
                var workingSetMB = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024);

                sw.Stop();

                var status = managedMB > AppConstants.Limits.MemoryCriticalMB ? HealthStatus.Unhealthy :
                             managedMB > AppConstants.Limits.MemoryWarningMB ? HealthStatus.Degraded :
                             HealthStatus.Healthy;

                return Task.FromResult(new HealthCheckResult
                {
                    Name = Name,
                    Status = status,
                    Description = $"{managedMB:F0} MB managed, {workingSetMB:F0} MB working set",
                    Duration = sw.Elapsed,
                    Data = new Dictionary<string, object>
                    {
                        ["ManagedMemoryMB"] = managedMB,
                        ["WorkingSetMB"] = workingSetMB,
                        ["Gen0Collections"] = GC.CollectionCount(0),
                        ["Gen1Collections"] = GC.CollectionCount(1),
                        ["Gen2Collections"] = GC.CollectionCount(2)
                    }
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new HealthCheckResult
                {
                    Name = Name,
                    Status = HealthStatus.Unhealthy,
                    Description = ex.Message,
                    Duration = sw.Elapsed
                });
            }
        }
    }

    /// <summary>
    /// FFmpeg varlık kontrolü
    /// </summary>
    public class FFmpegCheck : IHealthCheck
    {
        public string Name => "FFmpeg";

        public async Task<HealthCheckResult> CheckAsync(CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                var ffmpegPath = FindFFmpeg();

                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    return new HealthCheckResult
                    {
                        Name = Name,
                        Status = HealthStatus.Unhealthy,
                        Description = "FFmpeg bulunamadı",
                        Duration = sw.Elapsed
                    };
                }

                // FFmpeg version kontrolü
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return new HealthCheckResult
                    {
                        Name = Name,
                        Status = HealthStatus.Unhealthy,
                        Description = "FFmpeg başlatılamadı",
                        Duration = sw.Elapsed
                    };
                }

                var output = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);

                sw.Stop();

                return new HealthCheckResult
                {
                    Name = Name,
                    Status = HealthStatus.Healthy,
                    Description = output ?? "FFmpeg mevcut",
                    Duration = sw.Elapsed,
                    Data = new Dictionary<string, object>
                    {
                        ["Path"] = ffmpegPath,
                        ["Version"] = output ?? "unknown"
                    }
                };
            }
            catch (Exception ex)
            {
                return new HealthCheckResult
                {
                    Name = Name,
                    Status = HealthStatus.Unhealthy,
                    Description = ex.Message,
                    Duration = sw.Elapsed
                };
            }
        }

        private string? FindFFmpeg()
        {
            var paths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
                "ffmpeg.exe"
            };

            foreach (var path in paths)
            {
                if (File.Exists(path))
                    return path;
            }

            var envPath = Environment.GetEnvironmentVariable("PATH");
            if (envPath != null)
            {
                foreach (var dir in envPath.Split(Path.PathSeparator))
                {
                    var fullPath = Path.Combine(dir, "ffmpeg.exe");
                    if (File.Exists(fullPath))
                        return fullPath;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Network bağlantı kontrolü
    /// </summary>
    public class NetworkCheck : IHealthCheck
    {
        public string Name => "Network";

        public async Task<HealthCheckResult> CheckAsync(CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable())
                {
                    return new HealthCheckResult
                    {
                        Name = Name,
                        Status = HealthStatus.Unhealthy,
                        Description = "Ağ bağlantısı yok",
                        Duration = sw.Elapsed
                    };
                }

                using var ping = new Ping();
                var reply = await ping.SendPingAsync("8.8.8.8", 3000).ConfigureAwait(false);

                sw.Stop();

                var status = reply.Status == IPStatus.Success ? HealthStatus.Healthy : HealthStatus.Degraded;

                return new HealthCheckResult
                {
                    Name = Name,
                    Status = status,
                    Description = reply.Status == IPStatus.Success
                        ? $"Ping: {reply.RoundtripTime}ms"
                        : $"Ping failed: {reply.Status}",
                    Duration = sw.Elapsed,
                    Data = new Dictionary<string, object>
                    {
                        ["PingStatus"] = reply.Status.ToString(),
                        ["RoundtripTime"] = reply.RoundtripTime
                    }
                };
            }
            catch (Exception ex)
            {
                return new HealthCheckResult
                {
                    Name = Name,
                    Status = HealthStatus.Degraded,
                    Description = ex.Message,
                    Duration = sw.Elapsed
                };
            }
        }
    }

    /// <summary>
    /// License server erişilebilirlik kontrolü
    /// </summary>
    public class LicenseServerCheck : IHealthCheck
    {
        private readonly HttpClient _httpClient;

        public string Name => "LicenseServer";

        public LicenseServerCheck(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<HealthCheckResult> CheckAsync(CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                var serverUrl = Environment.GetEnvironmentVariable("UNICAST_LICENSE_SERVER")
                               ?? "https://license.unicast.app";

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(AppConstants.Timeouts.CancellationTimeoutSeconds));

                var response = await _httpClient.GetAsync($"{serverUrl}/health", cts.Token).ConfigureAwait(false);

                sw.Stop();

                return new HealthCheckResult
                {
                    Name = Name,
                    Status = response.IsSuccessStatusCode ? HealthStatus.Healthy : HealthStatus.Degraded,
                    Description = response.IsSuccessStatusCode
                        ? "License server erişilebilir"
                        : $"HTTP {(int)response.StatusCode}",
                    Duration = sw.Elapsed,
                    Data = new Dictionary<string, object>
                    {
                        ["StatusCode"] = (int)response.StatusCode,
                        ["ServerUrl"] = serverUrl
                    }
                };
            }
            catch (Exception ex)
            {
                return new HealthCheckResult
                {
                    Name = Name,
                    Status = HealthStatus.Degraded,
                    Description = ex.Message,
                    Duration = sw.Elapsed
                };
            }
        }
    }

    #endregion
}