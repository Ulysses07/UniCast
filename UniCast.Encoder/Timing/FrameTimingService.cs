using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace UniCast.Encoder.Timing
{
    /// <summary>
    /// Yüksek hassasiyetli frame timing yönetimi.
    /// V-Sync, frame pacing ve jitter reduction.
    /// 
    /// NEDEN ÖNEMLİ:
    /// - Düzgün frame delivery = smooth video
    /// - Jitter = video stuttering
    /// - Yanlış timing = dropped/duplicated frames
    /// </summary>
    public sealed class FrameTimingService : IDisposable
    {
        #region Native Imports (High Resolution Timer)

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uMilliseconds);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryTimerResolution(out uint min, out uint max, out uint current);

        [DllImport("ntdll.dll")]
        private static extern int NtSetTimerResolution(uint resolution, bool set, out uint current);

        #endregion

        #region Singleton

        private static readonly Lazy<FrameTimingService> _instance =
            new(() => new FrameTimingService(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static FrameTimingService Instance => _instance.Value;

        #endregion

        #region Properties

        /// <summary>
        /// Hedef FPS
        /// </summary>
        public double TargetFps { get; private set; } = 30;

        /// <summary>
        /// Frame başına süre (nanosaniye)
        /// </summary>
        public long FrameTimeNs { get; private set; }

        /// <summary>
        /// Frame başına süre (milisaniye)
        /// </summary>
        public double FrameTimeMs => FrameTimeNs / 1_000_000.0;

        /// <summary>
        /// Mevcut FPS (gerçek)
        /// </summary>
        public double ActualFps { get; private set; }

        /// <summary>
        /// Frame jitter (ms)
        /// </summary>
        public double JitterMs { get; private set; }

        /// <summary>
        /// Dropped frame sayısı
        /// </summary>
        public long DroppedFrames { get; private set; }

        /// <summary>
        /// Toplam frame sayısı
        /// </summary>
        public long TotalFrames { get; private set; }

        /// <summary>
        /// Drop rate (%)
        /// </summary>
        public double DropRate => TotalFrames > 0 ? (DroppedFrames * 100.0 / TotalFrames) : 0;

        /// <summary>
        /// High resolution timer aktif mi?
        /// </summary>
        public bool IsHighResolutionTimerEnabled { get; private set; }

        #endregion

        #region Fields

        private readonly Stopwatch _frameStopwatch = new();
        private readonly Stopwatch _statsStopwatch = new();

        private long _lastFrameTimestamp;
        private long _frameCount;
        private double _totalJitter;

        private bool _timerPeriodSet;
        private bool _disposed;

        // Circular buffer for frame times (son 60 frame)
        private readonly long[] _frameTimeHistory = new long[60];
        private int _frameTimeIndex;

        #endregion

        #region Constructor

        private FrameTimingService()
        {
            SetTargetFps(30);
            EnableHighResolutionTimer();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Hedef FPS ayarla
        /// </summary>
        public void SetTargetFps(double fps)
        {
            if (fps < 1 || fps > 240)
                throw new ArgumentOutOfRangeException(nameof(fps), "FPS 1-240 arasında olmalı");

            TargetFps = fps;
            FrameTimeNs = (long)(1_000_000_000.0 / fps);

            Debug.WriteLine($"[FrameTiming] Target FPS: {fps}, Frame time: {FrameTimeMs:F3}ms");
        }

        /// <summary>
        /// High resolution timer'ı etkinleştir
        /// Windows varsayılan timer resolution 15.6ms, bu 64 FPS'e kadar sınırlıyor
        /// 1ms resolution ile 1000 FPS'e kadar mümkün
        /// </summary>
        public void EnableHighResolutionTimer()
        {
            if (_timerPeriodSet) return;

            try
            {
                // Windows timer resolution'ı 1ms'e ayarla
                var result = timeBeginPeriod(1);
                _timerPeriodSet = result == 0;

                if (_timerPeriodSet)
                {
                    // Gerçek resolution'ı kontrol et
                    NtQueryTimerResolution(out uint min, out uint max, out uint current);
                    Debug.WriteLine($"[FrameTiming] Timer resolution: {current / 10000.0:F3}ms (min: {min / 10000.0:F3}ms)");
                    IsHighResolutionTimerEnabled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FrameTiming] High resolution timer error: {ex.Message}");
            }
        }

        /// <summary>
        /// Frame başlangıcını işaretle
        /// </summary>
        public void BeginFrame()
        {
            _frameStopwatch.Restart();

            if (!_statsStopwatch.IsRunning)
            {
                _statsStopwatch.Start();
            }
        }

        /// <summary>
        /// Frame bitişini işaretle ve gerekirse bekle
        /// </summary>
        /// <returns>Gerçek frame süresi (ms)</returns>
        public double EndFrame()
        {
            var elapsed = _frameStopwatch.Elapsed;
            var elapsedNs = elapsed.Ticks * 100; // 1 tick = 100ns

            TotalFrames++;

            // Hedef süreye ulaşmak için bekle
            var remainingNs = FrameTimeNs - elapsedNs;

            if (remainingNs > 0)
            {
                PreciseSleep(remainingNs);
            }
            else if (remainingNs < -FrameTimeNs) // 1 frame'den fazla geç
            {
                DroppedFrames++;
            }

            // Gerçek frame süresini hesapla
            var now = _frameStopwatch.ElapsedTicks * 100;
            var actualFrameTimeNs = now;

            if (_lastFrameTimestamp > 0)
            {
                var delta = actualFrameTimeNs - _lastFrameTimestamp;
                var jitter = Math.Abs(delta - FrameTimeNs);
                _totalJitter += jitter;
                JitterMs = _totalJitter / TotalFrames / 1_000_000.0;
            }

            // Frame time history güncelle
            _frameTimeHistory[_frameTimeIndex] = actualFrameTimeNs;
            _frameTimeIndex = (_frameTimeIndex + 1) % _frameTimeHistory.Length;

            _lastFrameTimestamp = actualFrameTimeNs;
            _frameCount++;

            // Her saniye istatistikleri güncelle
            if (_statsStopwatch.ElapsedMilliseconds >= 1000)
            {
                ActualFps = _frameCount * 1000.0 / _statsStopwatch.ElapsedMilliseconds;
                _frameCount = 0;
                _statsStopwatch.Restart();
            }

            return actualFrameTimeNs / 1_000_000.0;
        }

        /// <summary>
        /// Bir sonraki frame'e kadar bekle (async)
        /// </summary>
        public async Task WaitForNextFrameAsync(CancellationToken ct = default)
        {
            var elapsed = _frameStopwatch.Elapsed;
            var remainingMs = FrameTimeMs - elapsed.TotalMilliseconds;

            if (remainingMs > 1)
            {
                // Büyük beklemeler için Task.Delay
                await Task.Delay(TimeSpan.FromMilliseconds(remainingMs - 1), ct);
            }

            // Son kısım için spin-wait (daha hassas)
            while (_frameStopwatch.Elapsed.TotalMilliseconds < FrameTimeMs)
            {
                Thread.SpinWait(10);
            }
        }

        /// <summary>
        /// Adaptive frame timing - sistem yüküne göre ayarla
        /// </summary>
        public void AdaptiveTiming()
        {
            // Son 60 frame'in ortalamasını al
            double avgFrameTimeNs = 0;
            int count = 0;

            for (int i = 0; i < _frameTimeHistory.Length; i++)
            {
                if (_frameTimeHistory[i] > 0)
                {
                    avgFrameTimeNs += _frameTimeHistory[i];
                    count++;
                }
            }

            if (count > 0)
            {
                avgFrameTimeNs /= count;
                var avgFps = 1_000_000_000.0 / avgFrameTimeNs;

                // Eğer hedefin %90'ından düşükse, encoding çok yavaş
                if (avgFps < TargetFps * 0.9)
                {
                    Debug.WriteLine($"[FrameTiming] WARNING: Actual FPS ({avgFps:F1}) < Target ({TargetFps})");
                }
            }
        }

        /// <summary>
        /// İstatistikleri sıfırla
        /// </summary>
        public void ResetStats()
        {
            TotalFrames = 0;
            DroppedFrames = 0;
            JitterMs = 0;
            _totalJitter = 0;
            _frameCount = 0;
            _lastFrameTimestamp = 0;
            Array.Clear(_frameTimeHistory);
            _frameTimeIndex = 0;
            _statsStopwatch.Restart();
        }

        /// <summary>
        /// Detaylı istatistikleri al
        /// </summary>
        public FrameTimingStats GetStats()
        {
            return new FrameTimingStats
            {
                TargetFps = TargetFps,
                ActualFps = ActualFps,
                FrameTimeMs = FrameTimeMs,
                JitterMs = JitterMs,
                TotalFrames = TotalFrames,
                DroppedFrames = DroppedFrames,
                DropRate = DropRate,
                IsHighResolutionTimer = IsHighResolutionTimerEnabled
            };
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Yüksek hassasiyetli sleep
        /// Thread.Sleep yerine spin-wait + yield kombinasyonu
        /// </summary>
        private void PreciseSleep(long nanoseconds)
        {
            if (nanoseconds <= 0) return;

            var targetTicks = Stopwatch.GetTimestamp() + (nanoseconds * Stopwatch.Frequency / 1_000_000_000);

            // Büyük beklemeler için Thread.Sleep
            var milliseconds = nanoseconds / 1_000_000;
            if (milliseconds > 2)
            {
                Thread.Sleep((int)(milliseconds - 1));
            }

            // Son kısım için spin-wait (daha hassas, ama CPU kullanır)
            while (Stopwatch.GetTimestamp() < targetTicks)
            {
                Thread.SpinWait(1);
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_timerPeriodSet)
            {
                timeEndPeriod(1);
                _timerPeriodSet = false;
            }

            _frameStopwatch.Stop();
            _statsStopwatch.Stop();
        }

        #endregion
    }

    #region Frame Scheduler

    /// <summary>
    /// Frame scheduling ve pacing için yardımcı sınıf.
    /// V-Sync benzeri davranış sağlar.
    /// </summary>
    public sealed class FrameScheduler : IDisposable
    {
        private readonly FrameTimingService _timing;
        private readonly CancellationTokenSource _cts = new();

        private Task? _schedulerTask;
        private Action<long>? _onFrame;
        private bool _running;
        private bool _disposed;

        public bool IsRunning => _running;
        public long FrameNumber { get; private set; }

        public FrameScheduler(double targetFps = 30)
        {
            _timing = FrameTimingService.Instance;
            _timing.SetTargetFps(targetFps);
        }

        /// <summary>
        /// Frame scheduler'ı başlat
        /// </summary>
        /// <param name="onFrame">Her frame'de çağrılacak callback (frame number)</param>
        public void Start(Action<long> onFrame)
        {
            if (_running) return;

            _onFrame = onFrame;
            _running = true;
            _timing.ResetStats();

            _schedulerTask = Task.Run(SchedulerLoop, _cts.Token);
        }

        /// <summary>
        /// Frame scheduler'ı durdur
        /// </summary>
        public async Task StopAsync()
        {
            if (!_running) return;

            _running = false;
            _cts.Cancel();

            if (_schedulerTask != null)
            {
                try
                {
                    await _schedulerTask;
                }
                catch (OperationCanceledException) { }
            }
        }

        private void SchedulerLoop()
        {
            while (_running && !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    _timing.BeginFrame();

                    // Frame callback
                    _onFrame?.Invoke(FrameNumber++);

                    // Timing
                    _timing.EndFrame();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FrameScheduler] Frame error: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _running = false;
            _cts.Cancel();
            _cts.Dispose();
        }
    }

    #endregion

    #region Supporting Types

    public class FrameTimingStats
    {
        public double TargetFps { get; set; }
        public double ActualFps { get; set; }
        public double FrameTimeMs { get; set; }
        public double JitterMs { get; set; }
        public long TotalFrames { get; set; }
        public long DroppedFrames { get; set; }
        public double DropRate { get; set; }
        public bool IsHighResolutionTimer { get; set; }

        public override string ToString()
        {
            return $"FPS: {ActualFps:F1}/{TargetFps:F0} | " +
                   $"Jitter: {JitterMs:F2}ms | " +
                   $"Dropped: {DroppedFrames}/{TotalFrames} ({DropRate:F1}%)";
        }
    }

    #endregion
}
