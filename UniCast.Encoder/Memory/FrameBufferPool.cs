using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace UniCast.Encoder.Memory
{
    /// <summary>
    /// Video frame'leri için yüksek performanslı memory pool.
    /// 
    /// NEDEN ÖNEMLİ:
    /// - Her frame için new byte[] = GC pressure
    /// - GC collection = frame drop / stutter
    /// - Pooling = allocation yok, smooth video
    /// 
    /// Bu sınıf, 1080p60 streaming için saniyede 60 * 8MB = 480MB
    /// allocation'ı sıfıra indirir.
    /// </summary>
    public sealed class FrameBufferPool : IDisposable
    {
        #region Singleton

        private static readonly Lazy<FrameBufferPool> _instance =
            new(() => new FrameBufferPool(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static FrameBufferPool Instance => _instance.Value;

        #endregion

        #region Constants

        /// <summary>
        /// Desteklenen frame boyutları (pre-allocated pool'lar)
        /// </summary>
        public static class FrameSizes
        {
            // Width * Height * 4 (BGRA)
            public const int Size720p = 1280 * 720 * 4;      // ~3.7 MB
            public const int Size1080p = 1920 * 1080 * 4;    // ~8.3 MB
            public const int Size1440p = 2560 * 1440 * 4;    // ~14.7 MB
            public const int Size4K = 3840 * 2160 * 4;       // ~33.2 MB
        }

        private const int DefaultPoolSize = 8; // Her boyut için 8 buffer

        #endregion

        #region Properties

        /// <summary>
        /// Toplam pool boyutu (bytes)
        /// </summary>
        public long TotalPoolSizeBytes { get; private set; }

        /// <summary>
        /// Kullanımda olan buffer sayısı
        /// </summary>
        public int ActiveBufferCount => _activeCount;

        /// <summary>
        /// Pool'dan alınan toplam buffer sayısı
        /// </summary>
        public long TotalRentCount => _totalRentCount;

        /// <summary>
        /// Cache hit oranı (%)
        /// </summary>
        public double CacheHitRate => _totalRentCount > 0
            ? (_cacheHits * 100.0 / _totalRentCount)
            : 100;

        #endregion

        #region Fields

        // Boyut bazlı buffer pool'ları
        private readonly ConcurrentDictionary<int, ConcurrentBag<byte[]>> _pools = new();

        // ArrayPool for non-standard sizes
        private readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;

        // İstatistikler
        private int _activeCount;
        private long _totalRentCount;
        private long _cacheHits;

        // Pre-allocated pools
        private readonly ConcurrentBag<FrameBuffer> _720pPool = new();
        private readonly ConcurrentBag<FrameBuffer> _1080pPool = new();
        private readonly ConcurrentBag<FrameBuffer> _1440pPool = new();
        private readonly ConcurrentBag<FrameBuffer> _4kPool = new();

        private bool _disposed;

        #endregion

        #region Constructor

        private FrameBufferPool()
        {
            // Pre-allocate common sizes
            PreAllocate(FrameSizes.Size720p, DefaultPoolSize);
            PreAllocate(FrameSizes.Size1080p, DefaultPoolSize);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Belirli boyutta buffer al (pooled)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte[] Rent(int minimumSize)
        {
            Interlocked.Increment(ref _totalRentCount);
            Interlocked.Increment(ref _activeCount);

            // Standart boyutlar için özel pool'ları kontrol et
            var standardSize = GetStandardSize(minimumSize);

            if (_pools.TryGetValue(standardSize, out var pool) && pool.TryTake(out var buffer))
            {
                Interlocked.Increment(ref _cacheHits);
                return buffer;
            }

            // Pool'da yoksa yeni oluştur
            return new byte[standardSize];
        }

        /// <summary>
        /// Buffer'ı pool'a geri ver
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return(byte[] buffer)
        {
            if (buffer == null) return;

            Interlocked.Decrement(ref _activeCount);

            var pool = _pools.GetOrAdd(buffer.Length, _ => new ConcurrentBag<byte[]>());

            // Pool çok büyümüşse ekleme (memory leak önleme)
            if (pool.Count < DefaultPoolSize * 2)
            {
                pool.Add(buffer);
            }
        }

        /// <summary>
        /// FrameBuffer al (struct wrapper)
        /// DÜZELTME v32: NV12/YUV420 için doğru buffer boyutu ve stride hesaplama
        /// </summary>
        public FrameBuffer RentFrame(int width, int height, PixelFormat format = PixelFormat.BGRA)
        {
            int size;
            int stride;

            // DÜZELTME v32: NV12/YUV420 için özel hesaplama
            // YUV 4:2:0 formatı: Y plane (w*h) + UV interleaved (w*h/2) = w*h*1.5
            // Not: C#'ta 3/2 = 1 (integer division), bu yüzden w*h*3/2 kullanıyoruz
            if (format == PixelFormat.NV12 || format == PixelFormat.YUV420)
            {
                size = width * height * 3 / 2;  // 1.5 bytes per pixel average
                stride = width;                  // NV12/YUV420'de stride = width (Y plane)
            }
            else
            {
                var bytesPerPixel = format switch
                {
                    PixelFormat.BGRA => 4,
                    PixelFormat.BGR => 3,
                    _ => 4
                };
                size = width * height * bytesPerPixel;
                stride = width * bytesPerPixel;
            }

            var buffer = Rent(size);

            return new FrameBuffer
            {
                Data = buffer,
                Width = width,
                Height = height,
                Stride = stride,
                Format = format,
                Pool = this
            };
        }

        /// <summary>
        /// Pre-allocate belirli boyutta buffer'lar
        /// </summary>
        public void PreAllocate(int size, int count)
        {
            var pool = _pools.GetOrAdd(size, _ => new ConcurrentBag<byte[]>());

            for (int i = 0; i < count; i++)
            {
                pool.Add(new byte[size]);
            }

            TotalPoolSizeBytes += size * count;
            Debug.WriteLine($"[FrameBufferPool] Pre-allocated {count} buffers of {size / 1024}KB");
        }

        /// <summary>
        /// Pool istatistiklerini al
        /// </summary>
        public PoolStats GetStats()
        {
            long totalPooled = 0;
            foreach (var pool in _pools.Values)
            {
                totalPooled += pool.Count;
            }

            return new PoolStats
            {
                TotalPoolSizeBytes = TotalPoolSizeBytes,
                ActiveBuffers = _activeCount,
                PooledBuffers = (int)totalPooled,
                TotalRentCount = _totalRentCount,
                CacheHitRate = CacheHitRate
            };
        }

        /// <summary>
        /// Pool'u temizle
        /// </summary>
        public void Clear()
        {
            foreach (var pool in _pools.Values)
            {
                while (pool.TryTake(out _)) { }
            }

            TotalPoolSizeBytes = 0;
            _totalRentCount = 0;
            _cacheHits = 0;
        }

        #endregion

        #region Private Methods

        private int GetStandardSize(int requestedSize)
        {
            // En yakın standart boyuta yuvarla
            if (requestedSize <= FrameSizes.Size720p) return FrameSizes.Size720p;
            if (requestedSize <= FrameSizes.Size1080p) return FrameSizes.Size1080p;
            if (requestedSize <= FrameSizes.Size1440p) return FrameSizes.Size1440p;
            if (requestedSize <= FrameSizes.Size4K) return FrameSizes.Size4K;

            // 4K'dan büyükse exact size
            return requestedSize;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Clear();
            _pools.Clear();
        }

        #endregion
    }

    #region FrameBuffer Struct

    /// <summary>
    /// Pooled frame buffer wrapper.
    /// IDisposable pattern ile otomatik pool'a dönüş.
    /// </summary>
    public struct FrameBuffer : IDisposable
    {
        public byte[] Data { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public int Stride { get; internal set; }
        public PixelFormat Format { get; internal set; }
        internal FrameBufferPool? Pool { get; set; }

        /// <summary>
        /// Buffer'ı Span olarak al (zero-copy)
        /// </summary>
        public Span<byte> AsSpan() => Data.AsSpan();

        /// <summary>
        /// Buffer'ı Memory olarak al (async operations için)
        /// </summary>
        public Memory<byte> AsMemory() => Data.AsMemory();

        /// <summary>
        /// Belirli bir bölgeyi Span olarak al
        /// </summary>
        public Span<byte> AsSpan(int start, int length) => Data.AsSpan(start, length);

        /// <summary>
        /// Y plane'i al (NV12/YUV420 için)
        /// </summary>
        public Span<byte> GetYPlane()
        {
            if (Format != PixelFormat.NV12 && Format != PixelFormat.YUV420)
                throw new InvalidOperationException("Y plane only available for YUV formats");

            return Data.AsSpan(0, Width * Height);
        }

        /// <summary>
        /// UV plane'i al (NV12 için)
        /// </summary>
        public Span<byte> GetUVPlane()
        {
            if (Format != PixelFormat.NV12)
                throw new InvalidOperationException("UV plane only available for NV12 format");

            return Data.AsSpan(Width * Height, Width * Height / 2);
        }

        /// <summary>
        /// Total buffer size in bytes
        /// </summary>
        public int TotalSize => Data?.Length ?? 0;

        /// <summary>
        /// Buffer'ı sıfırla (debug için)
        /// </summary>
        public void Clear()
        {
            if (Data != null)
                Array.Clear(Data, 0, Data.Length);
        }

        /// <summary>
        /// Pool'a geri ver
        /// </summary>
        public void Dispose()
        {
            if (Pool != null && Data != null)
            {
                Pool.Return(Data);
                Data = null!;
            }
        }
    }

    #endregion

    #region Native Memory Pool

    /// <summary>
    /// Unmanaged memory pool for DirectX/interop operations.
    /// GC'den tamamen bağımsız, P/Invoke ve DirectX için ideal.
    /// </summary>
    public sealed class NativeMemoryPool : IDisposable
    {
        #region Singleton

        private static readonly Lazy<NativeMemoryPool> _instance =
            new(() => new NativeMemoryPool(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static NativeMemoryPool Instance => _instance.Value;

        #endregion

        #region Fields

        private readonly ConcurrentDictionary<int, ConcurrentBag<IntPtr>> _pools = new();
        private readonly ConcurrentDictionary<IntPtr, int> _allocations = new();

        private long _totalAllocated;
        private int _activeCount;
        private bool _disposed;

        #endregion

        #region Properties

        public long TotalAllocatedBytes => _totalAllocated;
        public int ActiveAllocations => _activeCount;

        #endregion

        #region Public Methods

        /// <summary>
        /// Native memory al
        /// </summary>
        public IntPtr Rent(int size)
        {
            Interlocked.Increment(ref _activeCount);

            // Pool'dan dene
            var standardSize = GetStandardSize(size);
            if (_pools.TryGetValue(standardSize, out var pool) && pool.TryTake(out var ptr))
            {
                return ptr;
            }

            // Yeni allocate
            ptr = Marshal.AllocHGlobal(standardSize);
            _allocations[ptr] = standardSize;
            Interlocked.Add(ref _totalAllocated, standardSize);

            return ptr;
        }

        /// <summary>
        /// Native memory'yi pool'a geri ver
        /// </summary>
        public void Return(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return;

            Interlocked.Decrement(ref _activeCount);

            if (_allocations.TryGetValue(ptr, out var size))
            {
                var pool = _pools.GetOrAdd(size, _ => new ConcurrentBag<IntPtr>());

                if (pool.Count < 16) // Max pool size
                {
                    pool.Add(ptr);
                    return;
                }
            }

            // Pool'a sığmadıysa free
            FreeNative(ptr);
        }

        /// <summary>
        /// Native memory'yi tamamen serbest bırak (pool'a koymadan)
        /// </summary>
        public void Free(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return;

            FreeNative(ptr);
        }

        #endregion

        #region Private Methods

        private int GetStandardSize(int requested)
        {
            // 4KB aligned sizes
            const int alignment = 4096;
            return ((requested + alignment - 1) / alignment) * alignment;
        }

        private void FreeNative(IntPtr ptr)
        {
            if (_allocations.TryRemove(ptr, out var size))
            {
                Interlocked.Add(ref _totalAllocated, -size);
            }

            Marshal.FreeHGlobal(ptr);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Pool'daki tüm pointer'ları free et
            foreach (var pool in _pools.Values)
            {
                while (pool.TryTake(out var ptr))
                {
                    FreeNative(ptr);
                }
            }

            // Aktif allocation'ları free et
            foreach (var ptr in _allocations.Keys)
            {
                Marshal.FreeHGlobal(ptr);
            }

            _pools.Clear();
            _allocations.Clear();
        }

        #endregion
    }

    #endregion

    #region Ring Buffer

    /// <summary>
    /// Lock-free ring buffer for producer-consumer patterns.
    /// Video pipeline'da frame passing için ideal.
    /// </summary>
    public sealed class RingBuffer<T> where T : class
    {
        private readonly T?[] _buffer;
        private readonly int _mask;

        private int _head; // Producer writes here
        private int _tail; // Consumer reads from here

        public int Capacity { get; }
        public int Count => (_head - _tail) & _mask;
        public bool IsEmpty => _head == _tail;
        public bool IsFull => Count == Capacity - 1;

        public RingBuffer(int capacity)
        {
            // Capacity must be power of 2
            capacity = NextPowerOfTwo(capacity);
            Capacity = capacity;
            _mask = capacity - 1;
            _buffer = new T[capacity];
        }

        /// <summary>
        /// Item ekle (producer)
        /// </summary>
        public bool TryEnqueue(T item)
        {
            var head = _head;
            var nextHead = (head + 1) & _mask;

            if (nextHead == Volatile.Read(ref _tail))
            {
                return false; // Full
            }

            _buffer[head] = item;
            Volatile.Write(ref _head, nextHead);
            return true;
        }

        /// <summary>
        /// Item al (consumer)
        /// </summary>
        public bool TryDequeue(out T? item)
        {
            var tail = _tail;

            if (tail == Volatile.Read(ref _head))
            {
                item = default;
                return false; // Empty
            }

            item = _buffer[tail];
            _buffer[tail] = default;
            Volatile.Write(ref _tail, (tail + 1) & _mask);
            return true;
        }

        /// <summary>
        /// Peek (remove etmeden bak)
        /// </summary>
        public bool TryPeek(out T? item)
        {
            var tail = _tail;

            if (tail == Volatile.Read(ref _head))
            {
                item = default;
                return false;
            }

            item = _buffer[tail];
            return true;
        }

        /// <summary>
        /// Tüm item'ları temizle
        /// </summary>
        public void Clear()
        {
            while (TryDequeue(out _)) { }
        }

        private static int NextPowerOfTwo(int v)
        {
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            return v + 1;
        }
    }

    #endregion

    #region Supporting Types

    public enum PixelFormat
    {
        BGRA,   // 32-bit (WPF, DirectX)
        BGR,    // 24-bit
        NV12,   // YUV 4:2:0 (hardware encoder input)
        YUV420  // YUV 4:2:0 planar
    }

    public class PoolStats
    {
        public long TotalPoolSizeBytes { get; set; }
        public int ActiveBuffers { get; set; }
        public int PooledBuffers { get; set; }
        public long TotalRentCount { get; set; }
        public double CacheHitRate { get; set; }

        public string TotalPoolSizeMB => $"{TotalPoolSizeBytes / (1024.0 * 1024.0):F1} MB";

        public override string ToString()
        {
            return $"Pool: {TotalPoolSizeMB} | Active: {ActiveBuffers} | Pooled: {PooledBuffers} | Hit Rate: {CacheHitRate:F1}%";
        }
    }

    #endregion
}