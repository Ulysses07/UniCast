using System;
using System.Collections.Generic;
using System.Threading;
using Serilog;

namespace UniCast.Core.Chat
{
    /// <summary>
    /// Çoklu kaynaktan gelen mesajları tek akışta birleştirir, dedupe eder ve rate-limit uygular.
    /// DÜZELTME: LRU cache ile verimli bellek yönetimi.
    /// </summary>
    public sealed class ChatBus : IDisposable
    {
        private readonly List<IChatIngestor> _ingestors = new();

        // DÜZELTME: LRU Cache - En eski mesajlar otomatik silinir
        private readonly LruCache<string> _seenMessages;

        private readonly Timer _statsTimer;
        private readonly int _maxPerSecond;
        private long _tickSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private int _countThisSecond = 0;

        // İstatistikler
        private long _totalReceived = 0;
        private long _totalDuplicate = 0;
        private long _totalRateLimited = 0;

        public event Action<ChatMessage>? OnMerged;

        /// <param name="maxPerSecond">Saniyede maksimum mesaj (varsayılan 20)</param>
        /// <param name="cacheCapacity">Dedupe cache kapasitesi (varsayılan 10000)</param>
        public ChatBus(int maxPerSecond = 20, int cacheCapacity = 10000)
        {
            _maxPerSecond = Math.Max(5, maxPerSecond);
            _seenMessages = new LruCache<string>(cacheCapacity);

            // Her dakika istatistik logla
            _statsTimer = new Timer(_ => LogStats(), null, 60_000, 60_000);
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
            Interlocked.Increment(ref _totalReceived);

            // Dedupe: Source + Id kombinasyonu
            var key = $"{m.Source}:{m.Id}";

            if (!_seenMessages.TryAdd(key))
            {
                Interlocked.Increment(ref _totalDuplicate);
                return; // Zaten gördük
            }

            // Rate limiting: Saniyede max mesaj
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
                return; // Rate limit aşıldı
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
                // Zaten var mı?
                if (_map.ContainsKey(item))
                {
                    return false;
                }

                // Kapasite dolmuşsa en eskiyi sil (LRU)
                while (_map.Count >= _capacity)
                {
                    var oldest = _list.Last;
                    if (oldest != null)
                    {
                        _map.Remove(oldest.Value);
                        _list.RemoveLast();
                    }
                }

                // Yeni öğeyi başa ekle (en yeni)
                var node = _list.AddFirst(item);
                _map[item] = node;

                return true;
            }
        }

        /// <summary>
        /// Öğenin cache'te olup olmadığını kontrol eder.
        /// Varsa öğeyi "en yeni" konumuna taşır.
        /// </summary>
        public bool Contains(T item)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(item, out var node))
                {
                    // En yeni konumuna taşı
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