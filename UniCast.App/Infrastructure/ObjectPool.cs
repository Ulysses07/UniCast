using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using Serilog;

namespace UniCast.App.Infrastructure
{
    /// <summary>
    /// DÜZELTME v19: Generic Object Pool
    /// Bellek kullanımını optimize eder, GC yükünü azaltır
    /// </summary>
    public sealed class ObjectPool<T> : IDisposable where T : class
    {
        #region Fields

        private readonly ConcurrentBag<T> _objects = new();
        private readonly Func<T> _objectFactory;
        private readonly Action<T>? _resetAction;
        private readonly int _maxSize;

        private int _currentCount;
        private int _totalCreated;
        private int _totalRented;
        private int _totalReturned;

        #endregion

        #region Properties

        public int CurrentCount => _currentCount;
        public int TotalCreated => _totalCreated;
        public int TotalRented => _totalRented;
        public int TotalReturned => _totalReturned;
        public int MaxSize => _maxSize;

        #endregion

        #region Constructor

        /// <summary>
        /// Object pool oluştur
        /// </summary>
        /// <param name="factory">Yeni nesne oluşturma fonksiyonu</param>
        /// <param name="resetAction">Nesne havuza döndürüldüğünde çalışacak reset fonksiyonu</param>
        /// <param name="maxSize">Maksimum havuz boyutu (varsayılan: işlemci sayısı * 2)</param>
        public ObjectPool(Func<T> factory, Action<T>? resetAction = null, int maxSize = 0)
        {
            _objectFactory = factory ?? throw new ArgumentNullException(nameof(factory));
            _resetAction = resetAction;
            _maxSize = maxSize > 0 ? maxSize : Environment.ProcessorCount * 2;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Havuzdan nesne al
        /// </summary>
        public T Rent()
        {
            Interlocked.Increment(ref _totalRented);

            if (_objects.TryTake(out var item))
            {
                Interlocked.Decrement(ref _currentCount);
                return item;
            }

            Interlocked.Increment(ref _totalCreated);
            return _objectFactory();
        }

        /// <summary>
        /// Nesneyi havuza geri ver
        /// </summary>
        public void Return(T item)
        {
            if (item == null) return;

            Interlocked.Increment(ref _totalReturned);

            // Reset action varsa çalıştır
            _resetAction?.Invoke(item);

            // Maksimum boyutu aşmadıysa havuza ekle
            if (_currentCount < _maxSize)
            {
                _objects.Add(item);
                Interlocked.Increment(ref _currentCount);
            }
            else
            {
                // Fazla nesneyi dispose et (IDisposable ise)
                if (item is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        /// <summary>
        /// Scoped kullanım - using bloğu sonunda otomatik return
        /// </summary>
        public PooledObject<T> RentScoped()
        {
            return new PooledObject<T>(this, Rent());
        }

        /// <summary>
        /// Havuzu temizle
        /// </summary>
        public void Clear()
        {
            while (_objects.TryTake(out var item))
            {
                if (item is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            _currentCount = 0;
            Log.Debug("[ObjectPool<{Type}>] Cleared", typeof(T).Name);
        }

        /// <summary>
        /// İstatistikleri al
        /// </summary>
        public PoolStatistics GetStatistics()
        {
            return new PoolStatistics
            {
                PoolType = typeof(T).Name,
                CurrentCount = _currentCount,
                MaxSize = _maxSize,
                TotalCreated = _totalCreated,
                TotalRented = _totalRented,
                TotalReturned = _totalReturned,
                HitRate = _totalRented > 0
                    ? (double)(_totalRented - _totalCreated) / _totalRented * 100
                    : 0
            };
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            Clear();
        }

        #endregion
    }

    #region PooledObject

    /// <summary>
    /// Scoped pooled object - using bloğu sonunda otomatik return
    /// </summary>
    public readonly struct PooledObject<T> : IDisposable where T : class
    {
        private readonly ObjectPool<T> _pool;

        public T Value { get; }

        internal PooledObject(ObjectPool<T> pool, T value)
        {
            _pool = pool;
            Value = value;
        }

        public void Dispose()
        {
            _pool.Return(Value);
        }

        public static implicit operator T(PooledObject<T> pooled) => pooled.Value;
    }

    #endregion

    #region Statistics

    public class PoolStatistics
    {
        public string PoolType { get; init; } = "";
        public int CurrentCount { get; init; }
        public int MaxSize { get; init; }
        public int TotalCreated { get; init; }
        public int TotalRented { get; init; }
        public int TotalReturned { get; init; }
        public double HitRate { get; init; }
    }

    #endregion

    #region Common Pools

    /// <summary>
    /// Sık kullanılan object pool'ları
    /// </summary>
    public static class CommonPools
    {
        /// <summary>
        /// StringBuilder pool
        /// </summary>
        public static readonly ObjectPool<System.Text.StringBuilder> StringBuilder =
            new(
                () => new System.Text.StringBuilder(256),
                sb => sb.Clear(),
                Environment.ProcessorCount * 4
            );

        /// <summary>
        /// MemoryStream pool
        /// </summary>
        public static readonly ObjectPool<System.IO.MemoryStream> MemoryStream =
            new(
                () => new System.IO.MemoryStream(4096),
                ms => { ms.SetLength(0); ms.Position = 0; },
                Environment.ProcessorCount * 2
            );

        /// <summary>
        /// List<string> pool
        /// </summary>
        public static readonly ObjectPool<System.Collections.Generic.List<string>> StringList =
            new(
                () => new System.Collections.Generic.List<string>(16),
                list => list.Clear(),
                Environment.ProcessorCount * 2
            );

        /// <summary>
        /// Dictionary<string, object> pool
        /// </summary>
        public static readonly ObjectPool<System.Collections.Generic.Dictionary<string, object>> StringObjectDict =
            new(
                () => new System.Collections.Generic.Dictionary<string, object>(8),
                dict => dict.Clear(),
                Environment.ProcessorCount * 2
            );
    }

    #endregion

    #region Buffer Pool Extensions

    /// <summary>
    /// ArrayPool extension metodları
    /// </summary>
    public static class BufferPoolExtensions
    {
        /// <summary>
        /// Scoped byte array - using bloğu sonunda otomatik return
        /// </summary>
        public static RentedBuffer<T> RentScoped<T>(this ArrayPool<T> pool, int minimumLength)
        {
            return new RentedBuffer<T>(pool, minimumLength);
        }
    }

    /// <summary>
    /// Scoped buffer wrapper
    /// </summary>
    public readonly struct RentedBuffer<T> : IDisposable
    {
        private readonly ArrayPool<T> _pool;

        public T[] Array { get; }
        public int Length { get; }

        internal RentedBuffer(ArrayPool<T> pool, int minimumLength)
        {
            _pool = pool;
            Array = pool.Rent(minimumLength);
            Length = minimumLength;
        }

        public Span<T> Span => Array.AsSpan(0, Length);
        public Memory<T> Memory => Array.AsMemory(0, Length);

        public void Dispose()
        {
            _pool.Return(Array);
        }
    }

    #endregion

    #region Pool Manager

    /// <summary>
    /// Merkezi pool yönetimi
    /// </summary>
    public static class PoolManager
    {
        private static readonly ConcurrentDictionary<Type, object> _pools = new();

        /// <summary>
        /// Tip için pool al veya oluştur
        /// </summary>
        public static ObjectPool<T> GetPool<T>(Func<T>? factory = null, Action<T>? resetAction = null)
            where T : class, new()
        {
            return (ObjectPool<T>)_pools.GetOrAdd(typeof(T), _ =>
                new ObjectPool<T>(factory ?? (() => new T()), resetAction));
        }

        /// <summary>
        /// Tüm pool istatistiklerini al
        /// </summary>
        public static System.Collections.Generic.IEnumerable<PoolStatistics> GetAllStatistics()
        {
            foreach (var pool in _pools.Values)
            {
                var method = pool.GetType().GetMethod("GetStatistics");
                if (method != null)
                {
                    var stats = method.Invoke(pool, null) as PoolStatistics;
                    if (stats != null)
                        yield return stats;
                }
            }
        }

        /// <summary>
        /// Tüm pool'ları temizle
        /// </summary>
        public static void ClearAll()
        {
            foreach (var pool in _pools.Values)
            {
                var method = pool.GetType().GetMethod("Clear");
                method?.Invoke(pool, null);
            }

            Log.Information("[PoolManager] All pools cleared");
        }
    }

    #endregion
}
