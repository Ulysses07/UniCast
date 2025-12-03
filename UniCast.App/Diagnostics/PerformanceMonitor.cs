using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Timer = System.Threading.Timer;

namespace UniCast.App.Diagnostics
{
    /// <summary>
    /// DÜZELTME v19: Performance Monitor
    /// CPU, memory, disk ve network kullanımını izler
    /// </summary>
    public sealed class PerformanceMonitor : IDisposable
    {
        #region Singleton

        private static readonly Lazy<PerformanceMonitor> _instance =
            new(() => new PerformanceMonitor(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static PerformanceMonitor Instance => _instance.Value;

        #endregion

        #region Configuration

        private static class Config
        {
            public const int SamplingIntervalMs = 1000;        // 1 saniye
            public const int MaxSamples = 300;                  // 5 dakikalık veri
            public const double CpuWarningThreshold = 80.0;     // %80
            public const double CpuCriticalThreshold = 95.0;    // %95
        }

        #endregion

        #region Fields

        private readonly Timer _samplingTimer;
        private readonly Process _currentProcess;
        private readonly ConcurrentQueue<PerformanceSample> _samples = new();
        private readonly Stopwatch _cpuStopwatch = new();

        private TimeSpan _lastCpuTime;
        private DateTime _lastSampleTime;
        private bool _isMonitoring;
        private bool _disposed;

        // Peak values
        private double _peakCpuPercent;
        private long _peakMemoryBytes;
        private long _peakThreadCount;

        #endregion

        #region Events

        public event EventHandler<PerformanceAlertEventArgs>? OnPerformanceAlert;

        #endregion

        #region Constructor

        private PerformanceMonitor()
        {
            _currentProcess = Process.GetCurrentProcess();
            _samplingTimer = new Timer(SamplePerformance, null, Timeout.Infinite, Timeout.Infinite);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// İzlemeyi başlat
        /// </summary>
        public void StartMonitoring()
        {
            if (_isMonitoring) return;

            _isMonitoring = true;
            _lastCpuTime = _currentProcess.TotalProcessorTime;
            _lastSampleTime = DateTime.UtcNow;
            _cpuStopwatch.Restart();

            _samplingTimer.Change(0, Config.SamplingIntervalMs);

            Log.Information("[PerfMonitor] Başlatıldı");
        }

        /// <summary>
        /// İzlemeyi durdur
        /// </summary>
        public void StopMonitoring()
        {
            if (!_isMonitoring) return;

            _isMonitoring = false;
            _samplingTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _cpuStopwatch.Stop();

            Log.Information("[PerfMonitor] Durduruldu");
        }

        /// <summary>
        /// Anlık performans verilerini al
        /// </summary>
        public PerformanceSnapshot GetSnapshot()
        {
            _currentProcess.Refresh();

            var cpuPercent = CalculateCpuPercent();

            return new PerformanceSnapshot
            {
                Timestamp = DateTime.UtcNow,
                CpuPercent = cpuPercent,
                MemoryMB = _currentProcess.WorkingSet64 / (1024 * 1024),
                PrivateMemoryMB = _currentProcess.PrivateMemorySize64 / (1024 * 1024),
                ManagedMemoryMB = GC.GetTotalMemory(false) / (1024 * 1024),
                ThreadCount = _currentProcess.Threads.Count,
                HandleCount = _currentProcess.HandleCount,
                PeakCpuPercent = _peakCpuPercent,
                PeakMemoryMB = _peakMemoryBytes / (1024 * 1024),
                ProcessUptime = DateTime.UtcNow - _currentProcess.StartTime.ToUniversalTime()
            };
        }

        /// <summary>
        /// Son N dakikanın ortalama CPU kullanımını al
        /// </summary>
        public double GetAverageCpu(int minutes = 1)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-minutes);
            var samples = _samples.Where(s => s.Timestamp > cutoff).ToList();

            if (samples.Count == 0) return 0;

            return samples.Average(s => s.CpuPercent);
        }

        /// <summary>
        /// Son N dakikanın ortalama memory kullanımını al
        /// </summary>
        public long GetAverageMemory(int minutes = 1)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-minutes);
            var samples = _samples.Where(s => s.Timestamp > cutoff).ToList();

            if (samples.Count == 0) return 0;

            return (long)samples.Average(s => s.MemoryBytes);
        }

        /// <summary>
        /// Performans trendini analiz et
        /// </summary>
        public PerformanceTrend AnalyzeTrend()
        {
            var samples = _samples.ToArray();
            if (samples.Length < 10)
            {
                return new PerformanceTrend { HasSufficientData = false };
            }

            var firstHalf = samples.Take(samples.Length / 2).ToArray();
            var secondHalf = samples.Skip(samples.Length / 2).ToArray();

            var cpuTrend = secondHalf.Average(s => s.CpuPercent) - firstHalf.Average(s => s.CpuPercent);
            var memoryTrend = secondHalf.Average(s => s.MemoryBytes) - firstHalf.Average(s => s.MemoryBytes);

            return new PerformanceTrend
            {
                HasSufficientData = true,
                CpuTrendPercent = cpuTrend,
                MemoryTrendMB = memoryTrend / (1024 * 1024),
                IsIncreasing = cpuTrend > 5 || memoryTrend > 50 * 1024 * 1024,
                AnalysisPeriod = samples[^1].Timestamp - samples[0].Timestamp
            };
        }

        /// <summary>
        /// Operasyon süresini ölç
        /// </summary>
        public OperationTimer MeasureOperation(string operationName)
        {
            return new OperationTimer(operationName);
        }

        /// <summary>
        /// İstatistikleri sıfırla
        /// </summary>
        public void ResetStatistics()
        {
            _peakCpuPercent = 0;
            _peakMemoryBytes = 0;
            _peakThreadCount = 0;

            while (_samples.TryDequeue(out _)) { }

            Log.Debug("[PerfMonitor] İstatistikler sıfırlandı");
        }

        #endregion

        #region Private Methods

        private void SamplePerformance(object? state)
        {
            try
            {
                _currentProcess.Refresh();

                var now = DateTime.UtcNow;
                var cpuPercent = CalculateCpuPercent();
                var memoryBytes = _currentProcess.WorkingSet64;
                var threadCount = _currentProcess.Threads.Count;

                var sample = new PerformanceSample
                {
                    Timestamp = now,
                    CpuPercent = cpuPercent,
                    MemoryBytes = memoryBytes,
                    ThreadCount = threadCount,
                    HandleCount = _currentProcess.HandleCount
                };

                _samples.Enqueue(sample);

                // Eski sample'ları temizle
                while (_samples.Count > Config.MaxSamples)
                {
                    _samples.TryDequeue(out _);
                }

                // Peak değerlerini güncelle
                if (cpuPercent > _peakCpuPercent) _peakCpuPercent = cpuPercent;
                if (memoryBytes > _peakMemoryBytes) _peakMemoryBytes = memoryBytes;
                if (threadCount > _peakThreadCount) _peakThreadCount = threadCount;

                // Alert kontrolleri
                CheckAlerts(cpuPercent, memoryBytes);

                _lastCpuTime = _currentProcess.TotalProcessorTime;
                _lastSampleTime = now;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[PerfMonitor] Sample hatası");
            }
        }

        private double CalculateCpuPercent()
        {
            try
            {
                var currentCpuTime = _currentProcess.TotalProcessorTime;
                var cpuUsed = currentCpuTime - _lastCpuTime;
                var elapsed = DateTime.UtcNow - _lastSampleTime;

                if (elapsed.TotalMilliseconds <= 0) return 0;

                // CPU yüzdesini hesapla (tüm çekirdekler üzerinden normalize)
                var cpuPercent = cpuUsed.TotalMilliseconds / elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;

                return Math.Min(100, Math.Max(0, cpuPercent));
            }
            catch
            {
                return 0;
            }
        }

        private void CheckAlerts(double cpuPercent, long memoryBytes)
        {
            if (cpuPercent >= Config.CpuCriticalThreshold)
            {
                OnPerformanceAlert?.Invoke(this, new PerformanceAlertEventArgs
                {
                    Level = AlertLevel.Critical,
                    Type = AlertType.HighCpu,
                    Value = cpuPercent,
                    Message = $"Kritik CPU kullanımı: {cpuPercent:F1}%"
                });
            }
            else if (cpuPercent >= Config.CpuWarningThreshold)
            {
                OnPerformanceAlert?.Invoke(this, new PerformanceAlertEventArgs
                {
                    Level = AlertLevel.Warning,
                    Type = AlertType.HighCpu,
                    Value = cpuPercent,
                    Message = $"Yüksek CPU kullanımı: {cpuPercent:F1}%"
                });
            }

            // Memory alert (1GB üstü)
            var memoryMB = memoryBytes / (1024 * 1024);
            if (memoryMB > 1000)
            {
                OnPerformanceAlert?.Invoke(this, new PerformanceAlertEventArgs
                {
                    Level = memoryMB > 1500 ? AlertLevel.Critical : AlertLevel.Warning,
                    Type = AlertType.HighMemory,
                    Value = memoryMB,
                    Message = $"Yüksek bellek kullanımı: {memoryMB} MB"
                });
            }
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopMonitoring();
            _samplingTimer.Dispose();
            _currentProcess.Dispose();

            OnPerformanceAlert = null;
        }

        #endregion
    }

    #region Types

    public class PerformanceSample
    {
        public DateTime Timestamp { get; init; }
        public double CpuPercent { get; init; }
        public long MemoryBytes { get; init; }
        public int ThreadCount { get; init; }
        public int HandleCount { get; init; }
    }

    public class PerformanceSnapshot
    {
        public DateTime Timestamp { get; init; }
        public double CpuPercent { get; init; }
        public long MemoryMB { get; init; }
        public long PrivateMemoryMB { get; init; }
        public long ManagedMemoryMB { get; init; }
        public int ThreadCount { get; init; }
        public int HandleCount { get; init; }
        public double PeakCpuPercent { get; init; }
        public long PeakMemoryMB { get; init; }
        public TimeSpan ProcessUptime { get; init; }

        public override string ToString() =>
            $"CPU: {CpuPercent:F1}%, Memory: {MemoryMB} MB, Threads: {ThreadCount}";
    }

    public class PerformanceTrend
    {
        public bool HasSufficientData { get; init; }
        public double CpuTrendPercent { get; init; }
        public double MemoryTrendMB { get; init; }
        public bool IsIncreasing { get; init; }
        public TimeSpan AnalysisPeriod { get; init; }
    }

    public enum AlertLevel
    {
        Info,
        Warning,
        Critical
    }

    public enum AlertType
    {
        HighCpu,
        HighMemory,
        HighDiskIO,
        HighNetworkIO
    }

    public class PerformanceAlertEventArgs : EventArgs
    {
        public AlertLevel Level { get; init; }
        public AlertType Type { get; init; }
        public double Value { get; init; }
        public string Message { get; init; } = "";
    }

    #endregion

    #region Operation Timer

    /// <summary>
    /// Operasyon süre ölçümü
    /// </summary>
    public sealed class OperationTimer : IDisposable
    {
        private readonly string _operationName;
        private readonly Stopwatch _stopwatch;
        private readonly DateTime _startTime;
        private bool _disposed;

        internal OperationTimer(string operationName)
        {
            _operationName = operationName;
            _startTime = DateTime.UtcNow;
            _stopwatch = Stopwatch.StartNew();
        }

        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _stopwatch.Stop();

            Log.Debug("[PerfMonitor] {Operation} tamamlandı: {ElapsedMs}ms",
                _operationName, _stopwatch.ElapsedMilliseconds);
        }
    }

    #endregion

    #region Frame Rate Monitor

    /// <summary>
    /// FPS / Frame rate izleme
    /// </summary>
    public class FrameRateMonitor
    {
        private readonly ConcurrentQueue<DateTime> _frameTimes = new();
        private const int MaxFrames = 120;

        public void RecordFrame()
        {
            _frameTimes.Enqueue(DateTime.UtcNow);

            while (_frameTimes.Count > MaxFrames)
            {
                _frameTimes.TryDequeue(out _);
            }
        }

        public double GetFps()
        {
            var frames = _frameTimes.ToArray();
            if (frames.Length < 2) return 0;

            var duration = frames[^1] - frames[0];
            if (duration.TotalSeconds <= 0) return 0;

            return (frames.Length - 1) / duration.TotalSeconds;
        }

        public FrameStats GetStats()
        {
            var frames = _frameTimes.ToArray();
            if (frames.Length < 2)
            {
                return new FrameStats();
            }

            var durations = new List<double>();
            for (int i = 1; i < frames.Length; i++)
            {
                durations.Add((frames[i] - frames[i - 1]).TotalMilliseconds);
            }

            durations.Sort();

            return new FrameStats
            {
                Fps = GetFps(),
                AvgFrameTimeMs = durations.Average(),
                MinFrameTimeMs = durations[0],
                MaxFrameTimeMs = durations[^1],
                P99FrameTimeMs = durations[(int)(durations.Count * 0.99)]
            };
        }
    }

    public class FrameStats
    {
        public double Fps { get; init; }
        public double AvgFrameTimeMs { get; init; }
        public double MinFrameTimeMs { get; init; }
        public double MaxFrameTimeMs { get; init; }
        public double P99FrameTimeMs { get; init; }
    }

    #endregion
}
