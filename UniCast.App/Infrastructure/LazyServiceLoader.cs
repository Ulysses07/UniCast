using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.App.Infrastructure
{
    /// <summary>
    /// DÜZELTME v19: Lazy Service Loading
    /// Uygulama başlangıç süresini optimize eder, servisleri ihtiyaç anında yükler
    /// </summary>
    public sealed class LazyServiceLoader
    {
        #region Singleton

        private static readonly Lazy<LazyServiceLoader> _instance =
            new(() => new LazyServiceLoader(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static LazyServiceLoader Instance => _instance.Value;

        #endregion

        #region Fields

        private readonly ConcurrentDictionary<Type, Lazy<object>> _services = new();
        private readonly ConcurrentDictionary<Type, Func<object>> _factories = new();
        private readonly ConcurrentDictionary<Type, ServiceLoadInfo> _loadInfo = new();
        private readonly List<(int Priority, Type Type, Func<Task> InitAction)> _backgroundInits = new();

        private bool _backgroundInitStarted;

        #endregion

        #region Events

        public event EventHandler<ServiceLoadedEventArgs>? OnServiceLoaded;

        #endregion

        #region Service Registration

        /// <summary>
        /// Lazy servis kaydı - ilk erişimde oluşturulur
        /// </summary>
        public void Register<T>(Func<T> factory) where T : class
        {
            _factories[typeof(T)] = () => factory();
            _services[typeof(T)] = new Lazy<object>(() =>
            {
                var sw = Stopwatch.StartNew();
                var instance = factory();
                sw.Stop();

                _loadInfo[typeof(T)] = new ServiceLoadInfo
                {
                    Type = typeof(T),
                    LoadTime = sw.Elapsed,
                    LoadedAt = DateTime.UtcNow
                };

                Log.Debug("[LazyLoader] Loaded: {Type} ({ElapsedMs}ms)",
                    typeof(T).Name, sw.ElapsedMilliseconds);

                OnServiceLoaded?.Invoke(this, new ServiceLoadedEventArgs
                {
                    ServiceType = typeof(T),
                    LoadTime = sw.Elapsed
                });

                return instance;
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>
        /// Eager servis kaydı - hemen oluşturulur
        /// </summary>
        public void RegisterEager<T>(T instance) where T : class
        {
            _services[typeof(T)] = new Lazy<object>(instance);
            _loadInfo[typeof(T)] = new ServiceLoadInfo
            {
                Type = typeof(T),
                LoadTime = TimeSpan.Zero,
                LoadedAt = DateTime.UtcNow,
                WasEager = true
            };
        }

        /// <summary>
        /// Background servis kaydı - uygulama başladıktan sonra yüklenir
        /// </summary>
        public void RegisterBackground<T>(Func<T> factory, int priority = 100) where T : class
        {
            Register(factory);

            _backgroundInits.Add((priority, typeof(T), async () =>
            {
                await Task.Run(() => Get<T>());
            }));
        }

        /// <summary>
        /// Async factory ile lazy kayıt
        /// </summary>
        public void RegisterAsync<T>(Func<Task<T>> factory) where T : class
        {
            _services[typeof(T)] = new Lazy<object>(() =>
            {
                var sw = Stopwatch.StartNew();
                var instance = factory().GetAwaiter().GetResult();
                sw.Stop();

                _loadInfo[typeof(T)] = new ServiceLoadInfo
                {
                    Type = typeof(T),
                    LoadTime = sw.Elapsed,
                    LoadedAt = DateTime.UtcNow
                };

                return instance;
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        #endregion

        #region Service Resolution

        /// <summary>
        /// Servisi al (lazy loading)
        /// </summary>
        public T Get<T>() where T : class
        {
            if (_services.TryGetValue(typeof(T), out var lazy))
            {
                return (T)lazy.Value;
            }

            throw new InvalidOperationException($"Service not registered: {typeof(T).Name}");
        }

        /// <summary>
        /// Servisi al veya null döndür
        /// </summary>
        public T? TryGet<T>() where T : class
        {
            if (_services.TryGetValue(typeof(T), out var lazy))
            {
                return (T)lazy.Value;
            }
            return null;
        }

        /// <summary>
        /// Servis kayıtlı mı
        /// </summary>
        public bool IsRegistered<T>() => _services.ContainsKey(typeof(T));

        /// <summary>
        /// Servis yüklendi mi
        /// </summary>
        public bool IsLoaded<T>() =>
            _services.TryGetValue(typeof(T), out var lazy) && lazy.IsValueCreated;

        #endregion

        #region Background Initialization

        /// <summary>
        /// Arkaplan yüklemelerini başlat
        /// </summary>
        public async Task StartBackgroundInitializationAsync(CancellationToken ct = default)
        {
            if (_backgroundInitStarted) return;
            _backgroundInitStarted = true;

            Log.Information("[LazyLoader] Background initialization başlıyor...");

            // Önceliğe göre sırala (düşük önce)
            _backgroundInits.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            foreach (var (priority, type, initAction) in _backgroundInits)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    await initAction();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[LazyLoader] Background init hatası: {Type}", type.Name);
                }
            }

            Log.Information("[LazyLoader] Background initialization tamamlandı");
        }

        #endregion

        #region Preloading

        /// <summary>
        /// Belirli servisleri önceden yükle
        /// </summary>
        public async Task PreloadAsync<T>(CancellationToken ct = default) where T : class
        {
            await Task.Run(() => Get<T>(), ct);
        }

        /// <summary>
        /// Birden fazla servisi paralel olarak önceden yükle
        /// </summary>
        public async Task PreloadManyAsync(IEnumerable<Type> types, CancellationToken ct = default)
        {
            var tasks = new List<Task>();

            foreach (var type in types)
            {
                if (_services.TryGetValue(type, out var lazy) && !lazy.IsValueCreated)
                {
                    tasks.Add(Task.Run(() => _ = lazy.Value, ct));
                }
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Tüm kayıtlı servisleri yükle
        /// </summary>
        public async Task PreloadAllAsync(CancellationToken ct = default)
        {
            await PreloadManyAsync(_services.Keys, ct);
        }

        #endregion

        #region Statistics

        /// <summary>
        /// Yükleme istatistiklerini al
        /// </summary>
        public LoadingStatistics GetStatistics()
        {
            var stats = new LoadingStatistics
            {
                TotalRegistered = _services.Count,
                TotalLoaded = 0,
                TotalLoadTime = TimeSpan.Zero,
                Services = new List<ServiceLoadInfo>()
            };

            foreach (var kvp in _services)
            {
                if (kvp.Value.IsValueCreated)
                {
                    stats.TotalLoaded++;

                    if (_loadInfo.TryGetValue(kvp.Key, out var info))
                    {
                        stats.TotalLoadTime += info.LoadTime;
                        stats.Services.Add(info);
                    }
                }
            }

            return stats;
        }

        /// <summary>
        /// En yavaş yüklenen servisleri al
        /// </summary>
        public IEnumerable<ServiceLoadInfo> GetSlowestServices(int count = 5)
        {
            return _loadInfo.Values
                .OrderByDescending(x => x.LoadTime)
                .Take(count);
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Belirli bir servisi sıfırla (bir sonraki erişimde yeniden yüklenir)
        /// </summary>
        public void Reset<T>() where T : class
        {
            if (_factories.TryGetValue(typeof(T), out var factory))
            {
                _services[typeof(T)] = new Lazy<object>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
                _loadInfo.TryRemove(typeof(T), out _);

                Log.Debug("[LazyLoader] Reset: {Type}", typeof(T).Name);
            }
        }

        /// <summary>
        /// Tüm servisleri sıfırla
        /// </summary>
        public void ResetAll()
        {
            foreach (var type in _factories.Keys)
            {
                if (_factories.TryGetValue(type, out var factory))
                {
                    _services[type] = new Lazy<object>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
                }
            }

            _loadInfo.Clear();
            Log.Information("[LazyLoader] All services reset");
        }

        #endregion
    }

    #region Types

    public class ServiceLoadInfo
    {
        public Type Type { get; init; } = null!;
        public TimeSpan LoadTime { get; init; }
        public DateTime LoadedAt { get; init; }
        public bool WasEager { get; init; }
    }

    public class LoadingStatistics
    {
        public int TotalRegistered { get; init; }
        public int TotalLoaded { get; set; }
        public TimeSpan TotalLoadTime { get; set; }
        public List<ServiceLoadInfo> Services { get; init; } = new();

        public double LoadPercentage => TotalRegistered > 0
            ? (double)TotalLoaded / TotalRegistered * 100
            : 0;
    }

    public class ServiceLoadedEventArgs : EventArgs
    {
        public Type ServiceType { get; init; } = null!;
        public TimeSpan LoadTime { get; init; }
    }

    #endregion

    #region Startup Optimizer

    /// <summary>
    /// Uygulama başlangıç optimizasyonu
    /// </summary>
    public static class StartupOptimizer
    {
        private static readonly Stopwatch _startupTimer = new();
        private static readonly List<(string Phase, TimeSpan Duration)> _phases = new();

        /// <summary>
        /// Başlangıç süresini ölçmeye başla
        /// </summary>
        public static void Start()
        {
            _startupTimer.Restart();
        }

        /// <summary>
        /// Faz tamamlandığını kaydet
        /// </summary>
        public static void RecordPhase(string phaseName)
        {
            _phases.Add((phaseName, _startupTimer.Elapsed));
            Log.Debug("[Startup] {Phase}: {Elapsed}ms", phaseName, _startupTimer.ElapsedMilliseconds);
        }

        /// <summary>
        /// Başlangıç tamamlandı
        /// </summary>
        public static TimeSpan Complete()
        {
            _startupTimer.Stop();

            Log.Information("[Startup] Tamamlandı. Toplam: {TotalMs}ms", _startupTimer.ElapsedMilliseconds);

            return _startupTimer.Elapsed;
        }

        /// <summary>
        /// Başlangıç raporu al
        /// </summary>
        public static StartupReport GetReport()
        {
            return new StartupReport
            {
                TotalTime = _startupTimer.Elapsed,
                Phases = _phases.ToList()
            };
        }
    }

    public class StartupReport
    {
        public TimeSpan TotalTime { get; init; }
        public List<(string Phase, TimeSpan Duration)> Phases { get; init; } = new();
    }

    #endregion
}
