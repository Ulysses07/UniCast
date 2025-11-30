using System;
using System.Collections.Generic;
using System.Threading;
using Serilog;

namespace UniCast.Core.Chat
{
    /// <summary>
    /// Çoklu kaynaktan gelen mesajları tek akışta birleştirir, dedupe eder ve rate-limit uygular.
    /// DÜZELTME: Hardcoded değerler yerine Constants kullanımı.
    /// </summary>
    public sealed class ChatBus : IDisposable
    {
        private readonly List<IChatIngestor> _ingestors = [];
        private readonly LruCache<string> _seenMessages;
        private readonly Timer _statsTimer;
        private readonly int _maxPerSecond;
        private long _tickSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private int _countThisSecond = 0;
        private bool _disposed;

        // İstatistikler
        private long _totalReceived = 0;
        private long _totalDuplicate = 0;
        private long _totalRateLimited = 0;

        public event Action<ChatMessage>? OnMerged;

        /// <summary>
        /// ChatBus oluşturur.
        /// </summary>
        /// <param name="maxPerSecond">Saniyede maksimum mesaj (varsayılan: ChatConstants.MaxMessagesPerSecond)</param>
        /// <param name="cacheCapacity">Dedupe cache kapasitesi (varsayılan: ChatConstants.CacheCapacity)</param>
        public ChatBus(int maxPerSecond = 0, int cacheCapacity = 0)
        {
            // DÜZELTME: Constants kullanımı - 0 veya negatif ise varsayılan değerleri kullan
            _maxPerSecond = maxPerSecond > 0 ? maxPerSecond : ChatConstants.MaxMessagesPerSecond;
            var capacity = cacheCapacity > 0 ? cacheCapacity : ChatConstants.CacheCapacity;

            _seenMessages = new LruCache<string>(capacity);

            // İstatistik loglama aralığı Constants'tan
            _statsTimer = new Timer(_ => LogStats(), null, ChatConstants.StatsIntervalMs, ChatConstants.StatsIntervalMs);
        }

        public void Attach(IChatIngestor ingestor)
        {
            if (ingestor == null) return;

            lock (_ingestors)
            {
                if (_ingestors.Contains(ingestor)) return;
                ingestor.OnMessage += HandleIncoming;
                _ingestors.Add(ingestor);
            }

            Log.Debug("[ChatBus] Ingestor eklendi: {Name}", ingestor.Name);
        }

        public void Detach(IChatIngestor ingestor)
        {
            if (ingestor == null) return;

            lock (_ingestors)
            {
                if (_ingestors.Remove(ingestor))
                {
                    ingestor.OnMessage -= HandleIncoming;
                    Log.Debug("[ChatBus] Ingestor çıkarıldı: {Name}", ingestor.Name);
                }
            }
        }

        private void HandleIncoming(ChatMessage m)
        {
            if (_disposed) return;

            Interlocked.Increment(ref _totalReceived);

            // Dedupe: Source + Id kombinasyonu
            var key = $"{m.Source}:{m.Id}";

            if (!_seenMessages.TryAdd(key))
            {
                Interlocked.Increment(ref _totalDuplicate);
                return;
            }

            // Rate limiting
            var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (nowSec != Interlocked.Read(ref _tickSecond))
            {
                Interlocked.Exchange(ref _tickSecond, nowSec);
                Interlocked.Exchange(ref _countThisSecond, 0);
            }

            var count = Interlocked.Increment(ref _countThisSecond);
            if (count > _maxPerSecond)
            {
                Interlocked.Increment(ref _totalRateLimited);
                return;
            }

            // Mesajı ilet
            try
            {
                OnMerged?.Invoke(m);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ChatBus] OnMerged handler hatası");
            }
        }

        private void LogStats()
        {
            if (_disposed) return;

            var received = Interlocked.Read(ref _totalReceived);
            var duplicate = Interlocked.Read(ref _totalDuplicate);
            var limited = Interlocked.Read(ref _totalRateLimited);
            var cacheSize = _seenMessages.Count;

            if (received > 0)
            {
                Log.Information(
                    "[ChatBus] Stats: Received={Received}, Duplicate={Duplicate}, RateLimited={Limited}, CacheSize={Cache}",
                    received, duplicate, limited, cacheSize);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _statsTimer.Dispose();

            lock (_ingestors)
            {
                foreach (var i in _ingestors)
                {
                    i.OnMessage -= HandleIncoming;
                }
                _ingestors.Clear();
            }

            _seenMessages.Clear();
            OnMerged = null;
        }
    }

    /// <summary>
    /// Chat sistemi sabitleri.
    /// DÜZELTME: Hardcoded değerler yerine merkezi sabitler.
    /// </summary>
    public static class ChatConstants
    {
        /// <summary>Saniyede maksimum mesaj (rate limit)</summary>
        public const int MaxMessagesPerSecond = 20;

        /// <summary>Dedupe cache kapasitesi</summary>
        public const int CacheCapacity = 10000;

        /// <summary>Overlay'de gösterilecek maksimum mesaj</summary>
        public const int MaxOverlayMessages = 8;

        /// <summary>UI'da gösterilecek maksimum mesaj</summary>
        public const int MaxUiMessages = 1000;

        /// <summary>İstatistik loglama aralığı (ms)</summary>
        public const int StatsIntervalMs = 60000;
    }

    /// <summary>
    /// Thread-safe LRU (Least Recently Used) Cache.
    /// Kapasite dolunca en eski öğeler otomatik silinir.
    /// </summary>
    internal sealed class LruCache<T> where T : notnull
    {
        private readonly int _capacity;
        private readonly Dictionary<T, LinkedListNode<T>> _map;
        private readonly LinkedList<T> _list;
        private readonly object _lock = new();

        public int Count
        {
            get { lock (_lock) return _map.Count; }
        }

        public LruCache(int capacity)
        {
            _capacity = Math.Max(100, capacity);
            _map = new Dictionary<T, LinkedListNode<T>>(_capacity);
            _list = new LinkedList<T>();
        }

        /// <summary>
        /// Öğeyi cache'e ekler. Zaten varsa false döner.
        /// </summary>
        public bool TryAdd(T item)
        {
            lock (_lock)
            {
                if (_map.ContainsKey(item))
                {
                    return false;
                }

                while (_map.Count >= _capacity)
                {
                    var oldest = _list.Last;
                    if (oldest != null)
                    {
                        _map.Remove(oldest.Value);
                        _list.RemoveLast();
                    }
                }

                var node = _list.AddFirst(item);
                _map[item] = node;

                return true;
            }
        }

        /// <summary>
        /// Öğenin cache'te olup olmadığını kontrol eder.
        /// </summary>
        public bool Contains(T item)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(item, out var node))
                {
                    _list.Remove(node);
                    _list.AddFirst(node);
                    return true;
                }
                return false;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _map.Clear();
                _list.Clear();
            }
        }
    }
}