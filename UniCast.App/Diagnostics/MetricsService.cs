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
    /// DÜZELTME v19: Metrics ve Telemetry servisi
    /// Performans metrikleri, kullanım istatistikleri, timing ölçümleri
    /// </summary>
    public sealed class MetricsService : IDisposable
    {
        #region Singleton

        private static readonly Lazy<MetricsService> _instance =
            new(() => new MetricsService(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static MetricsService Instance => _instance.Value;

        #endregion

        #region Fields

        private readonly ConcurrentDictionary<string, Counter> _counters = new();
        private readonly ConcurrentDictionary<string, Gauge> _gauges = new();
        private readonly ConcurrentDictionary<string, Histogram> _histograms = new();
        private readonly ConcurrentDictionary<string, Timer> _timers = new();

        private readonly Timer _flushTimer;
        private readonly List<IMetricsSink> _sinks = new();
        private bool _disposed;

        private const int FlushIntervalMs = 60000; // 1 dakika

        #endregion

        #region Constructor

        private MetricsService()
        {
            _flushTimer = new Timer(FlushMetrics, null, FlushIntervalMs, FlushIntervalMs);

            // Varsayılan sink ekle (Log)
            _sinks.Add(new LogMetricsSink());
        }

        #endregion

        #region Counter Operations

        /// <summary>
        /// Counter'ı artır
        /// </summary>
        public void Increment(string name, long value = 1, Dictionary<string, string>? tags = null)
        {
            var counter = _counters.GetOrAdd(name, _ => new Counter(name));
            counter.Add(value);
        }

        /// <summary>
        /// Counter değerini al
        /// </summary>
        public long GetCount(string name)
        {
            return _counters.TryGetValue(name, out var counter) ? counter.Value : 0;
        }

        #endregion

        #region Gauge Operations

        /// <summary>
        /// Gauge değerini ayarla
        /// </summary>
        public void SetGauge(string name, double value, Dictionary<string, string>? tags = null)
        {
            var gauge = _gauges.GetOrAdd(name, _ => new Gauge(name));
            gauge.Set(value);
        }

        /// <summary>
        /// Gauge değerini al
        /// </summary>
        public double GetGauge(string name)
        {
            return _gauges.TryGetValue(name, out var gauge) ? gauge.Value : 0;
        }

        #endregion

        #region Histogram Operations

        /// <summary>
        /// Histogram'a değer ekle
        /// </summary>
        public void RecordHistogram(string name, double value, Dictionary<string, string>? tags = null)
        {
            var histogram = _histograms.GetOrAdd(name, _ => new Histogram(name));
            histogram.Record(value);
        }

        /// <summary>
        /// Histogram istatistiklerini al
        /// </summary>
        public HistogramStats? GetHistogramStats(string name)
        {
            return _histograms.TryGetValue(name, out var histogram) ? histogram.GetStats() : null;
        }

        #endregion

        #region Timer Operations

        /// <summary>
        /// Süre ölçümü başlat
        /// </summary>
        public IDisposable StartTimer(string name, Dictionary<string, string>? tags = null)
        {
            return new TimerScope(this, name, tags);
        }

        /// <summary>
        /// Süre kaydet
        /// </summary>
        public void RecordTime(string name, TimeSpan duration, Dictionary<string, string>? tags = null)
        {
            RecordHistogram($"{name}.duration_ms", duration.TotalMilliseconds, tags);
            Increment($"{name}.count", 1, tags);
        }

        /// <summary>
        /// Async operation ölçümü
        /// </summary>
        public async Task<T> MeasureAsync<T>(string name, Func<Task<T>> operation)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                return await operation();
            }
            finally
            {
                sw.Stop();
                RecordTime(name, sw.Elapsed);
            }
        }

        /// <summary>
        /// Sync operation ölçümü
        /// </summary>
        public T Measure<T>(string name, Func<T> operation)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                return operation();
            }
            finally
            {
                sw.Stop();
                RecordTime(name, sw.Elapsed);
            }
        }

        #endregion

        #region Common Metrics Helpers

        /// <summary>
        /// Stream başlangıcı
        /// </summary>
        public void RecordStreamStart(string platform)
        {
            Increment("stream.starts", 1, new Dictionary<string, string> { ["platform"] = platform });
            SetGauge("stream.active", 1);
        }

        /// <summary>
        /// Stream bitişi
        /// </summary>
        public void RecordStreamEnd(string platform, TimeSpan duration)
        {
            Increment("stream.ends", 1, new Dictionary<string, string> { ["platform"] = platform });
            RecordHistogram("stream.duration_minutes", duration.TotalMinutes);
            SetGauge("stream.active", 0);
        }

        /// <summary>
        /// Chat mesajı
        /// </summary>
        public void RecordChatMessage(string platform)
        {
            Increment("chat.messages", 1, new Dictionary<string, string> { ["platform"] = platform });
        }

        /// <summary>
        /// Hata
        /// </summary>
        public void RecordError(string component, string errorType)
        {
            Increment("errors.total", 1, new Dictionary<string, string>
            {
                ["component"] = component,
                ["type"] = errorType
            });
        }

        /// <summary>
        /// API çağrısı
        /// </summary>
        public void RecordApiCall(string endpoint, int statusCode, TimeSpan duration)
        {
            RecordHistogram($"api.{endpoint}.duration_ms", duration.TotalMilliseconds);
            Increment($"api.{endpoint}.calls");
            
            if (statusCode >= 400)
            {
                Increment($"api.{endpoint}.errors");
            }
        }

        #endregion

        #region Sink Management

        /// <summary>
        /// Metrik sink ekle
        /// </summary>
        public void AddSink(IMetricsSink sink)
        {
            _sinks.Add(sink);
        }

        /// <summary>
        /// Metrikleri flush et
        /// </summary>
        public void Flush()
        {
            FlushMetrics(null);
        }

        private void FlushMetrics(object? state)
        {
            try
            {
                var snapshot = new MetricsSnapshot
                {
                    Timestamp = DateTime.UtcNow,
                    Counters = _counters.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value),
                    Gauges = _gauges.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value),
                    Histograms = _histograms.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.GetStats())
                };

                foreach (var sink in _sinks)
                {
                    try
                    {
                        sink.Write(snapshot);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[Metrics] Sink yazma hatası: {SinkType}", sink.GetType().Name);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Metrics] Flush hatası");
            }
        }

        #endregion

        #region Reporting

        /// <summary>
        /// Tüm metriklerin özet raporu
        /// </summary>
        public MetricsReport GetReport()
        {
            return new MetricsReport
            {
                GeneratedAt = DateTime.UtcNow,
                Counters = _counters.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value),
                Gauges = _gauges.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value),
                Histograms = _histograms.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.GetStats())
            };
        }

        /// <summary>
        /// Metrikleri sıfırla
        /// </summary>
        public void Reset()
        {
            _counters.Clear();
            _gauges.Clear();
            _histograms.Clear();

            Log.Information("[Metrics] Metrikler sıfırlandı");
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Flush();
            _flushTimer.Dispose();
            _sinks.Clear();
        }

        #endregion
    }

    #region Metric Types

    public class Counter
    {
        public string Name { get; }
        private long _value;

        public long Value => Interlocked.Read(ref _value);

        public Counter(string name) => Name = name;

        public void Add(long value) => Interlocked.Add(ref _value, value);
        public void Reset() => Interlocked.Exchange(ref _value, 0);
    }

    public class Gauge
    {
        public string Name { get; }
        private double _value;

        public double Value => _value;

        public Gauge(string name) => Name = name;

        public void Set(double value) => Interlocked.Exchange(ref _value, value);
    }

    public class Histogram
    {
        public string Name { get; }
        private readonly ConcurrentQueue<double> _values = new();
        private const int MaxSamples = 1000;

        public Histogram(string name) => Name = name;

        public void Record(double value)
        {
            _values.Enqueue(value);

            while (_values.Count > MaxSamples)
            {
                _values.TryDequeue(out _);
            }
        }

        public HistogramStats GetStats()
        {
            var values = _values.ToArray();
            if (values.Length == 0)
            {
                return new HistogramStats();
            }

            Array.Sort(values);

            return new HistogramStats
            {
                Count = values.Length,
                Min = values[0],
                Max = values[^1],
                Mean = values.Average(),
                Median = values[values.Length / 2],
                P95 = values[(int)(values.Length * 0.95)],
                P99 = values[(int)(values.Length * 0.99)]
            };
        }
    }

    public class HistogramStats
    {
        public int Count { get; init; }
        public double Min { get; init; }
        public double Max { get; init; }
        public double Mean { get; init; }
        public double Median { get; init; }
        public double P95 { get; init; }
        public double P99 { get; init; }
    }

    public class TimerScope : IDisposable
    {
        private readonly MetricsService _metrics;
        private readonly string _name;
        private readonly Dictionary<string, string>? _tags;
        private readonly Stopwatch _sw;

        public TimerScope(MetricsService metrics, string name, Dictionary<string, string>? tags)
        {
            _metrics = metrics;
            _name = name;
            _tags = tags;
            _sw = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _sw.Stop();
            _metrics.RecordTime(_name, _sw.Elapsed, _tags);
        }
    }

    #endregion

    #region Sink Interface & Implementations

    public interface IMetricsSink
    {
        void Write(MetricsSnapshot snapshot);
    }

    public class MetricsSnapshot
    {
        public DateTime Timestamp { get; init; }
        public Dictionary<string, long> Counters { get; init; } = new();
        public Dictionary<string, double> Gauges { get; init; } = new();
        public Dictionary<string, HistogramStats> Histograms { get; init; } = new();
    }

    public class MetricsReport
    {
        public DateTime GeneratedAt { get; init; }
        public Dictionary<string, long> Counters { get; init; } = new();
        public Dictionary<string, double> Gauges { get; init; } = new();
        public Dictionary<string, HistogramStats> Histograms { get; init; } = new();
    }

    /// <summary>
    /// Log sink - metrikleri Serilog'a yazar
    /// </summary>
    public class LogMetricsSink : IMetricsSink
    {
        public void Write(MetricsSnapshot snapshot)
        {
            if (snapshot.Counters.Count == 0 && snapshot.Gauges.Count == 0)
                return;

            Log.Debug("[Metrics] Snapshot: Counters={CounterCount}, Gauges={GaugeCount}, Histograms={HistogramCount}",
                snapshot.Counters.Count,
                snapshot.Gauges.Count,
                snapshot.Histograms.Count);
        }
    }

    /// <summary>
    /// File sink - metrikleri dosyaya yazar
    /// </summary>
    public class FileMetricsSink : IMetricsSink
    {
        private readonly string _filePath;

        public FileMetricsSink(string filePath)
        {
            _filePath = filePath;
        }

        public void Write(MetricsSnapshot snapshot)
        {
            try
            {
                var line = System.Text.Json.JsonSerializer.Serialize(snapshot);
                System.IO.File.AppendAllText(_filePath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Metrics] File sink yazma hatası");
            }
        }
    }

    #endregion
}
