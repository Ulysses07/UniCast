using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Timer = System.Threading.Timer;

namespace UniCast.App.Diagnostics
{
    /// <summary>
    /// DÜZELTME v19: Memory profiling ve monitoring
    /// GC istatistikleri, memory leak detection, bellek optimizasyonu
    /// </summary>
    public sealed class MemoryProfiler : IDisposable
    {
        #region Singleton

        private static readonly Lazy<MemoryProfiler> _instance =
            new(() => new MemoryProfiler(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static MemoryProfiler Instance => _instance.Value;

        #endregion

        #region Configuration

        private static class Config
        {
            public const int SamplingIntervalMs = 5000;        // 5 saniye
            public const long WarningThresholdMB = 500;        // 500 MB
            public const long CriticalThresholdMB = 1000;      // 1 GB
            public const int MaxSamples = 720;                  // 1 saat (5sn interval)
            public const double LeakDetectionGrowthRate = 0.1; // %10 artış
        }

        #endregion

        #region Fields

        private readonly Timer _samplingTimer;
        private readonly ConcurrentQueue<MemorySample> _samples = new();
        private readonly Process _currentProcess;
        private readonly CancellationTokenSource _cts = new(); // DÜZELTME v43: CancellationToken desteği
        private bool _isMonitoring;
        private bool _disposed;

        private long _peakWorkingSet;
        private long _peakManagedMemory;
        private int _gen0Collections;
        private int _gen1Collections;
        private int _gen2Collections;

        #endregion

        #region Events

        public event EventHandler<MemoryWarningEventArgs>? OnMemoryWarning;
        public event EventHandler<MemoryLeakDetectedEventArgs>? OnMemoryLeakDetected;

        #endregion

        #region Constructor

        private MemoryProfiler()
        {
            _currentProcess = Process.GetCurrentProcess();
            _samplingTimer = new Timer(SampleMemory, null, Timeout.Infinite, Timeout.Infinite);

            // GC notification'ları kaydet
            RegisterGCNotifications();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Memory monitoring'i başlat
        /// </summary>
        public void StartMonitoring()
        {
            if (_isMonitoring) return;

            _isMonitoring = true;
            _samplingTimer.Change(0, Config.SamplingIntervalMs);

            Log.Information("[MemoryProfiler] Monitoring başlatıldı");
        }

        /// <summary>
        /// Memory monitoring'i durdur
        /// </summary>
        public void StopMonitoring()
        {
            if (!_isMonitoring) return;

            _isMonitoring = false;
            _samplingTimer.Change(Timeout.Infinite, Timeout.Infinite);

            Log.Information("[MemoryProfiler] Monitoring durduruldu");
        }

        /// <summary>
        /// Anlık memory snapshot al
        /// </summary>
        public MemorySnapshot GetSnapshot()
        {
            _currentProcess.Refresh();

            return new MemorySnapshot
            {
                Timestamp = DateTime.UtcNow,
                WorkingSetMB = _currentProcess.WorkingSet64 / (1024 * 1024),
                PrivateMemoryMB = _currentProcess.PrivateMemorySize64 / (1024 * 1024),
                ManagedMemoryMB = GC.GetTotalMemory(false) / (1024 * 1024),
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
                PeakWorkingSetMB = _peakWorkingSet / (1024 * 1024),
                PeakManagedMemoryMB = _peakManagedMemory / (1024 * 1024),
                GCLatencyMode = GCSettings.LatencyMode,
                IsServerGC = GCSettings.IsServerGC,
                ThreadCount = _currentProcess.Threads.Count
            };
        }

        /// <summary>
        /// GC zorla çalıştır (dikkatli kullan!)
        /// </summary>
        public void ForceGarbageCollection(bool blocking = false)
        {
            Log.Debug("[MemoryProfiler] Manuel GC tetikleniyor...");

            var before = GC.GetTotalMemory(false);

            if (blocking)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            else
            {
                GC.Collect(2, GCCollectionMode.Optimized, false);
            }

            var after = GC.GetTotalMemory(false);
            var freedMB = (before - after) / (1024 * 1024);

            Log.Information("[MemoryProfiler] GC tamamlandı. Serbest bırakılan: {FreedMB} MB", freedMB);
        }

        /// <summary>
        /// Large Object Heap'i sıkıştır
        /// </summary>
        public void CompactLargeObjectHeap()
        {
            Log.Debug("[MemoryProfiler] LOH sıkıştırılıyor...");

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();

            Log.Information("[MemoryProfiler] LOH sıkıştırma tamamlandı");
        }

        /// <summary>
        /// Memory leak analizi yap
        /// </summary>
        public MemoryLeakAnalysis AnalyzeForLeaks()
        {
            var samples = _samples.ToArray();
            if (samples.Length < 10)
            {
                return new MemoryLeakAnalysis { HasSufficientData = false };
            }

            // Trend analizi - son 10 sample
            var recentSamples = samples.Length > 10
                ? samples[^10..]
                : samples;

            var firstSample = recentSamples[0];
            var lastSample = recentSamples[^1];

            var growthRate = (double)(lastSample.ManagedMemoryBytes - firstSample.ManagedMemoryBytes)
                            / firstSample.ManagedMemoryBytes;

            var isPotentialLeak = growthRate > Config.LeakDetectionGrowthRate;

            return new MemoryLeakAnalysis
            {
                HasSufficientData = true,
                GrowthRate = growthRate,
                IsPotentialLeak = isPotentialLeak,
                FirstSampleMB = firstSample.ManagedMemoryBytes / (1024 * 1024),
                LastSampleMB = lastSample.ManagedMemoryBytes / (1024 * 1024),
                AnalysisPeriod = lastSample.Timestamp - firstSample.Timestamp,
                Recommendation = isPotentialLeak
                    ? "Potansiyel memory leak algılandı. Object allocation'ları kontrol edin."
                    : "Normal bellek kullanımı."
            };
        }

        /// <summary>
        /// Memory istatistiklerini sıfırla
        /// </summary>
        public void ResetStatistics()
        {
            _peakWorkingSet = 0;
            _peakManagedMemory = 0;
            _gen0Collections = GC.CollectionCount(0);
            _gen1Collections = GC.CollectionCount(1);
            _gen2Collections = GC.CollectionCount(2);

            while (_samples.TryDequeue(out _)) { }

            Log.Debug("[MemoryProfiler] İstatistikler sıfırlandı");
        }

        #endregion

        #region Private Methods

        private void SampleMemory(object? state)
        {
            try
            {
                _currentProcess.Refresh();

                var managedMemory = GC.GetTotalMemory(false);
                var workingSet = _currentProcess.WorkingSet64;

                // Peak değerlerini güncelle
                if (workingSet > _peakWorkingSet)
                    _peakWorkingSet = workingSet;

                if (managedMemory > _peakManagedMemory)
                    _peakManagedMemory = managedMemory;

                // Sample kaydet
                var sample = new MemorySample
                {
                    Timestamp = DateTime.UtcNow,
                    ManagedMemoryBytes = managedMemory,
                    WorkingSetBytes = workingSet,
                    Gen0Count = GC.CollectionCount(0),
                    Gen1Count = GC.CollectionCount(1),
                    Gen2Count = GC.CollectionCount(2)
                };

                _samples.Enqueue(sample);

                // Eski sample'ları temizle
                while (_samples.Count > Config.MaxSamples)
                {
                    _samples.TryDequeue(out _);
                }

                // Threshold kontrolleri
                var memoryMB = managedMemory / (1024 * 1024);

                if (memoryMB >= Config.CriticalThresholdMB)
                {
                    OnMemoryWarning?.Invoke(this, new MemoryWarningEventArgs
                    {
                        Level = MemoryWarningLevel.Critical,
                        CurrentMemoryMB = memoryMB,
                        ThresholdMB = Config.CriticalThresholdMB,
                        Message = "Kritik bellek kullanımı!"
                    });

                    // Otomatik GC tetikle
                    ForceGarbageCollection(false);
                }
                else if (memoryMB >= Config.WarningThresholdMB)
                {
                    OnMemoryWarning?.Invoke(this, new MemoryWarningEventArgs
                    {
                        Level = MemoryWarningLevel.Warning,
                        CurrentMemoryMB = memoryMB,
                        ThresholdMB = Config.WarningThresholdMB,
                        Message = "Yüksek bellek kullanımı"
                    });
                }

                // Leak detection
                if (_samples.Count >= 10 && _samples.Count % 10 == 0)
                {
                    var analysis = AnalyzeForLeaks();
                    if (analysis.IsPotentialLeak)
                    {
                        OnMemoryLeakDetected?.Invoke(this, new MemoryLeakDetectedEventArgs
                        {
                            Analysis = analysis
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MemoryProfiler] Sample hatası");
            }
        }

        private void RegisterGCNotifications()
        {
            try
            {
                // GC notification için background task
                // DÜZELTME v43: Sadece burst GC'leri logla (spam önleme)
                Task.Run(async () =>
                {
                    int lastLoggedGen2 = _gen2Collections;
                    DateTime lastLogTime = DateTime.UtcNow;

                    while (!_disposed)
                    {
                        var currentGen0 = GC.CollectionCount(0);
                        var currentGen1 = GC.CollectionCount(1);
                        var currentGen2 = GC.CollectionCount(2);

                        // DÜZELTME v43: Sadece şu durumlarda logla:
                        // 1. Son 30 saniyede 5+ Gen2 GC olduysa (burst)
                        // 2. Veya en az 60 saniye geçtiyse (periyodik rapor)
                        var gen2Diff = currentGen2 - lastLoggedGen2;
                        var timeSinceLastLog = DateTime.UtcNow - lastLogTime;

                        if (gen2Diff >= 5 || (gen2Diff > 0 && timeSinceLastLog.TotalSeconds >= 60))
                        {
                            if (gen2Diff >= 5)
                            {
                                Log.Warning("[MemoryProfiler] Gen2 GC burst: {Count} GC in {Seconds}s (total: #{Total})",
                                    gen2Diff, (int)timeSinceLastLog.TotalSeconds, currentGen2);
                            }
                            else
                            {
                                Log.Debug("[MemoryProfiler] Gen2 GC özeti: {Count} GC (total: #{Total})",
                                    gen2Diff, currentGen2);
                            }
                            lastLoggedGen2 = currentGen2;
                            lastLogTime = DateTime.UtcNow;
                        }

                        _gen0Collections = currentGen0;
                        _gen1Collections = currentGen1;
                        _gen2Collections = currentGen2;

                        // DÜZELTME v43: CancellationToken ile iptal edilebilir delay
                        try
                        {
                            await Task.Delay(1000, _cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break; // Normal shutdown
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MemoryProfiler] GC notification kaydı hatası");
            }
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // DÜZELTME v43: CancellationToken ile GC notification task'ını durdur
            try { _cts.Cancel(); } catch { }

            StopMonitoring();
            _samplingTimer.Dispose();
            _currentProcess.Dispose();
            _cts.Dispose(); // DÜZELTME v43

            OnMemoryWarning = null;
            OnMemoryLeakDetected = null;
        }

        #endregion
    }

    #region Data Types

    public class MemorySnapshot
    {
        public DateTime Timestamp { get; init; }
        public long WorkingSetMB { get; init; }
        public long PrivateMemoryMB { get; init; }
        public long ManagedMemoryMB { get; init; }
        public int Gen0Collections { get; init; }
        public int Gen1Collections { get; init; }
        public int Gen2Collections { get; init; }
        public long PeakWorkingSetMB { get; init; }
        public long PeakManagedMemoryMB { get; init; }
        public GCLatencyMode GCLatencyMode { get; init; }
        public bool IsServerGC { get; init; }
        public int ThreadCount { get; init; }

        public override string ToString() =>
            $"Memory: {ManagedMemoryMB}MB managed, {WorkingSetMB}MB working set, " +
            $"GC: {Gen0Collections}/{Gen1Collections}/{Gen2Collections}";
    }

    public class MemorySample
    {
        public DateTime Timestamp { get; init; }
        public long ManagedMemoryBytes { get; init; }
        public long WorkingSetBytes { get; init; }
        public int Gen0Count { get; init; }
        public int Gen1Count { get; init; }
        public int Gen2Count { get; init; }
    }

    public class MemoryLeakAnalysis
    {
        public bool HasSufficientData { get; init; }
        public double GrowthRate { get; init; }
        public bool IsPotentialLeak { get; init; }
        public long FirstSampleMB { get; init; }
        public long LastSampleMB { get; init; }
        public TimeSpan AnalysisPeriod { get; init; }
        public string? Recommendation { get; init; }
    }

    public enum MemoryWarningLevel
    {
        Normal,
        Warning,
        Critical
    }

    public class MemoryWarningEventArgs : EventArgs
    {
        public MemoryWarningLevel Level { get; init; }
        public long CurrentMemoryMB { get; init; }
        public long ThresholdMB { get; init; }
        public string Message { get; init; } = "";
    }

    public class MemoryLeakDetectedEventArgs : EventArgs
    {
        public MemoryLeakAnalysis Analysis { get; init; } = null!;
    }

    #endregion
}