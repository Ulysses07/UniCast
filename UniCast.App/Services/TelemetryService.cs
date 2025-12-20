using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Timer = System.Threading.Timer;

namespace UniCast.App.Services
{
    /// <summary>
    /// Telemetry Service - Anonim kullanım verileri ve crash raporlama.
    /// GDPR uyumlu, opt-out destekli, minimal veri toplama.
    /// DÜZELTME v50: SemaphoreSlim dispose hatası düzeltildi.
    /// </summary>
    public sealed class TelemetryService : IDisposable
    {
        #region Singleton

        private static readonly Lazy<TelemetryService> _instance =
            new(() => new TelemetryService(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static TelemetryService Instance => _instance.Value;

        #endregion

        #region Constants

        private const int MaxQueueSize = 100;
        private const int FlushIntervalSeconds = 60;
        private const int MaxRetries = 3;

        #endregion

        #region Fields

        private readonly HttpClient _httpClient;
        private readonly ConcurrentQueue<TelemetryEvent> _eventQueue = new();
        private readonly SemaphoreSlim _flushLock = new(1, 1);
        private readonly string _sessionId;
        private readonly string _anonymousId;
        private readonly Stopwatch _sessionTimer = new();

        private Timer? _flushTimer;
        private volatile bool _disposed;  // DÜZELTME v50: volatile eklendi
        private bool _enabled = true;

        #endregion

        #region Properties

        /// <summary>
        /// Telemetry sunucu URL'i
        /// </summary>
        public string ServerUrl { get; set; } = "https://license.unicastapp.com/api/v1/telemetry";

        /// <summary>
        /// Telemetry aktif mi (kullanıcı ayarı)
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                Log.Information("[Telemetry] {Status}", value ? "Enabled" : "Disabled");
            }
        }

        /// <summary>
        /// Oturum süresi
        /// </summary>
        public TimeSpan SessionDuration => _sessionTimer.Elapsed;

        /// <summary>
        /// Kuyrukta bekleyen event sayısı
        /// </summary>
        public int PendingEvents => _eventQueue.Count;

        #endregion

        #region Constructor

        private TelemetryService()
        {
            _sessionId = Guid.NewGuid().ToString("N")[..16];
            _anonymousId = GenerateAnonymousId();

            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "UniCast-Telemetry/1.0");

            _sessionTimer.Start();

            Log.Debug("[Telemetry] Service initialized. Session: {SessionId}", _sessionId);
        }

        #endregion

        #region Public API - Initialization

        /// <summary>
        /// Servisi başlatır
        /// </summary>
        public void Initialize()
        {
            if (!_enabled || _disposed) return;

            // Global exception handler
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // Flush timer başlat
            _flushTimer = new Timer(
                async _ => await FlushAsync().ConfigureAwait(false),
                null,
                TimeSpan.FromSeconds(FlushIntervalSeconds),
                TimeSpan.FromSeconds(FlushIntervalSeconds)
            );

            // App start event
            TrackEvent("app_started", new Dictionary<string, object>
            {
                ["version"] = GetAppVersion(),
                ["os"] = GetOsInfo(),
                ["runtime"] = RuntimeInformation.FrameworkDescription
            });

            Log.Information("[Telemetry] Service started");
        }

        /// <summary>
        /// Uygulama kapanırken çağrılır
        /// </summary>
        public async Task ShutdownAsync()
        {
            if (!_enabled || _disposed) return;

            TrackEvent("app_closed", new Dictionary<string, object>
            {
                ["session_duration_seconds"] = (int)SessionDuration.TotalSeconds
            });

            await FlushAsync().ConfigureAwait(false);
            Log.Information("[Telemetry] Service shutdown. Session duration: {Duration}", SessionDuration);
        }

        #endregion

        #region Public API - Event Tracking

        /// <summary>
        /// Özel event kaydeder
        /// </summary>
        public void TrackEvent(string eventName, Dictionary<string, object>? properties = null)
        {
            if (!_enabled || _disposed) return;

            var telemetryEvent = new TelemetryEvent
            {
                Type = TelemetryEventType.Event,
                Name = eventName,
                Timestamp = DateTime.UtcNow,
                SessionId = _sessionId,
                AnonymousId = _anonymousId,
                Properties = properties ?? new()
            };

            EnqueueEvent(telemetryEvent);
            Log.Debug("[Telemetry] Event tracked: {EventName}", eventName);
        }

        /// <summary>
        /// Özellik kullanımını kaydeder
        /// </summary>
        public void TrackFeatureUsage(string featureName)
        {
            TrackEvent("feature_used", new Dictionary<string, object>
            {
                ["feature"] = featureName
            });
        }

        /// <summary>
        /// Yayın başlangıcını kaydeder
        /// </summary>
        public void TrackStreamStarted(string[] platforms, string encoder, string resolution)
        {
            TrackEvent("stream_started", new Dictionary<string, object>
            {
                ["platforms"] = platforms,
                ["platform_count"] = platforms.Length,
                ["encoder"] = encoder,
                ["resolution"] = resolution
            });
        }

        /// <summary>
        /// Yayın bitişini kaydeder
        /// </summary>
        public void TrackStreamEnded(int durationSeconds, long totalBytes)
        {
            TrackEvent("stream_ended", new Dictionary<string, object>
            {
                ["duration_seconds"] = durationSeconds,
                ["total_mb"] = totalBytes / (1024 * 1024)
            });
        }

        /// <summary>
        /// Hata kaydeder (crash olmayan)
        /// </summary>
        public void TrackError(string errorType, string message, string? stackTrace = null)
        {
            if (!_enabled || _disposed) return;

            var telemetryEvent = new TelemetryEvent
            {
                Type = TelemetryEventType.Error,
                Name = "error",
                Timestamp = DateTime.UtcNow,
                SessionId = _sessionId,
                AnonymousId = _anonymousId,
                Properties = new Dictionary<string, object>
                {
                    ["error_type"] = errorType,
                    ["message"] = TruncateString(message, 500),
                    ["stack_trace"] = TruncateString(stackTrace ?? "", 2000)
                }
            };

            EnqueueEvent(telemetryEvent);
            Log.Debug("[Telemetry] Error tracked: {ErrorType}", errorType);
        }

        /// <summary>
        /// Crash kaydeder
        /// </summary>
        public void TrackCrash(Exception exception)
        {
            if (!_enabled || _disposed) return;

            var telemetryEvent = new TelemetryEvent
            {
                Type = TelemetryEventType.Crash,
                Name = "crash",
                Timestamp = DateTime.UtcNow,
                SessionId = _sessionId,
                AnonymousId = _anonymousId,
                Properties = new Dictionary<string, object>
                {
                    ["exception_type"] = exception.GetType().FullName ?? "Unknown",
                    ["message"] = TruncateString(exception.Message, 500),
                    ["stack_trace"] = TruncateString(exception.StackTrace ?? "", 4000),
                    ["session_duration_seconds"] = (int)SessionDuration.TotalSeconds
                }
            };

            // Crash'ler hemen gönderilmeli
            EnqueueEvent(telemetryEvent);
            _ = FlushAsync();

            Log.Error(exception, "[Telemetry] Crash tracked");
        }

        /// <summary>
        /// Performans metriği kaydeder
        /// </summary>
        public void TrackMetric(string metricName, double value, string? unit = null)
        {
            if (!_enabled || _disposed) return;

            var telemetryEvent = new TelemetryEvent
            {
                Type = TelemetryEventType.Metric,
                Name = metricName,
                Timestamp = DateTime.UtcNow,
                SessionId = _sessionId,
                AnonymousId = _anonymousId,
                Properties = new Dictionary<string, object>
                {
                    ["value"] = value,
                    ["unit"] = unit ?? ""
                }
            };

            EnqueueEvent(telemetryEvent);
        }

        #endregion

        #region Private Methods

        private void EnqueueEvent(TelemetryEvent telemetryEvent)
        {
            if (_disposed) return;  // DÜZELTME v50: Dispose kontrolü

            // Queue doluysa eski event'leri at
            while (_eventQueue.Count >= MaxQueueSize)
            {
                _eventQueue.TryDequeue(out _);
            }

            _eventQueue.Enqueue(telemetryEvent);
        }

        private async Task FlushAsync()
        {
            // DÜZELTME v50: Dispose kontrolü en başta
            if (_disposed || !_enabled || _eventQueue.IsEmpty) return;

            // DÜZELTME v50: Try-catch ile semaphore erişimi
            bool lockAcquired = false;
            try
            {
                // Dispose edilmişse çık
                if (_disposed) return;

                lockAcquired = await _flushLock.WaitAsync(0).ConfigureAwait(false);
                if (!lockAcquired)
                {
                    return; // Zaten flush yapılıyor
                }

                // Lock alındıktan sonra tekrar kontrol
                if (_disposed) return;

                var events = new List<TelemetryEvent>();
                while (_eventQueue.TryDequeue(out var evt) && events.Count < 50)
                {
                    events.Add(evt);
                }

                if (events.Count == 0) return;

                var payload = new TelemetryPayload
                {
                    Events = events,
                    AppVersion = GetAppVersion(),
                    Platform = "windows"
                };

                for (int retry = 0; retry < MaxRetries; retry++)
                {
                    // DÜZELTME v50: Her retry'da dispose kontrolü
                    if (_disposed) return;

                    try
                    {
                        var response = await _httpClient.PostAsJsonAsync(ServerUrl, payload).ConfigureAwait(false);

                        if (response.IsSuccessStatusCode)
                        {
                            Log.Debug("[Telemetry] Flushed {Count} events", events.Count);
                            return;
                        }

                        Log.Warning("[Telemetry] Server returned {StatusCode}", response.StatusCode);
                    }
                    catch (ObjectDisposedException)
                    {
                        // DÜZELTME v50: HttpClient dispose edilmiş, çık
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[Telemetry] Flush attempt {Retry} failed", retry + 1);

                        if (_disposed) return;
                        await Task.Delay(1000 * (retry + 1)).ConfigureAwait(false);
                    }
                }

                // Başarısız olursa event'leri geri kuyruğa ekle (dispose edilmemişse)
                if (!_disposed)
                {
                    foreach (var evt in events)
                    {
                        _eventQueue.Enqueue(evt);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // DÜZELTME v50: SemaphoreSlim dispose edilmiş, sessizce çık
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Telemetry] FlushAsync error");
            }
            finally
            {
                // DÜZELTME v50: Release öncesi dispose kontrolü
                if (lockAcquired && !_disposed)
                {
                    try
                    {
                        _flushLock.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Dispose edilmiş, yok say
                    }
                    catch (SemaphoreFullException)
                    {
                        // Zaten release edilmiş, yok say
                    }
                }
            }
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                TrackCrash(ex);
            }
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            TrackCrash(e.Exception);
            e.SetObserved();
        }

        private static string GenerateAnonymousId()
        {
            // Makine bazlı anonim ID (gizlilik korumalı)
            var machineData = Environment.MachineName + Environment.UserName + Environment.OSVersion;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(machineData));
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }

        private static string GetAppVersion()
        {
            return System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString() ?? "1.0.0";
        }

        private static string GetOsInfo()
        {
            return $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}";
        }

        private static string TruncateString(string input, int maxLength)
        {
            if (string.IsNullOrEmpty(input)) return "";
            return input.Length <= maxLength ? input : input[..maxLength] + "...";
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;  // DÜZELTME v50: Önce flag'i set et

            // Event handler'ları kaldır
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

            // DÜZELTME v50: Önce timer'ı durdur
            _flushTimer?.Dispose();
            _flushTimer = null;

            // DÜZELTME v50: Kısa bir süre bekle (devam eden flush için)
            Thread.Sleep(100);

            // Kaynakları dispose et
            try
            {
                _flushLock.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TelemetryService] FlushLock dispose error: {ex.Message}");
            }

            try
            {
                _httpClient.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TelemetryService] HttpClient dispose error: {ex.Message}");
            }

            _sessionTimer.Stop();

            Log.Debug("[Telemetry] Service disposed");
        }

        #endregion
    }

    #region Models

    public enum TelemetryEventType
    {
        Event,
        Error,
        Crash,
        Metric
    }

    public class TelemetryEvent
    {
        public TelemetryEventType Type { get; set; }
        public string Name { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string SessionId { get; set; } = "";
        public string AnonymousId { get; set; } = "";
        public Dictionary<string, object> Properties { get; set; } = new();
    }

    public class TelemetryPayload
    {
        public List<TelemetryEvent> Events { get; set; } = new();
        public string AppVersion { get; set; } = "";
        public string Platform { get; set; } = "";
    }

    #endregion
}