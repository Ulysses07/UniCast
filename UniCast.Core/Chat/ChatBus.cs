using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace UniCast.Core.Chat
{
    /// <summary>
    /// Çoklu kaynaktan gelen mesajları tek akışta birleştirir, dedupe eder ve rate-limit uygular.
    /// UI ve overlay aynı bus üzerinden beslenir.
    /// </summary>
    public sealed class ChatBus : IDisposable
    {
        private readonly List<IChatIngestor> _ingestors = new();
        private readonly ConcurrentDictionary<string, byte> _seen = new(); // Id dedupe
        private readonly Timer _gcTimer;
        private readonly int _maxPerSecond;
        private long _tickSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private int _countThisSecond = 0;

        public event Action<ChatMessage>? OnMerged;

        /// <param name="maxPerSecond">Aşırı mesaj akışını dengelemek için limit (varsayılan 20/s)</param>
        public ChatBus(int maxPerSecond = 20)
        {
            _maxPerSecond = Math.Max(5, maxPerSecond);
            _gcTimer = new Timer(_ => CompactSeen(), null, 60_000, 60_000);
        }

        public void Attach(IChatIngestor ingestor)
        {
            if (_ingestors.Contains(ingestor)) return;
            ingestor.OnMessage += HandleIncoming;
            _ingestors.Add(ingestor);
        }

        private void HandleIncoming(ChatMessage m)
        {
            // Dedupe by Source + Id
            var key = $"{m.Source}:{m.Id}";
            if (!_seen.TryAdd(key, 1)) return;

            // Simple per-second rate limit
            var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (nowSec != _tickSecond)
            {
                Interlocked.Exchange(ref _tickSecond, nowSec);
                Interlocked.Exchange(ref _countThisSecond, 0);
            }
            var count = Interlocked.Increment(ref _countThisSecond);
            if (count > _maxPerSecond) return;

            OnMerged?.Invoke(m);
        }

        private void CompactSeen()
        {
            // Çok büyümesin diye hafifçe temizle
            if (_seen.Count > 50_000)
            {
                var keep = _seen.Keys.Take(10_000).ToList();
                _seen.Clear();
                foreach (var k in keep) _seen.TryAdd(k, 1);
            }
        }

        public void Dispose()
        {
            _gcTimer.Dispose();
            foreach (var i in _ingestors)
            {
                i.OnMessage -= HandleIncoming;
            }
            _ingestors.Clear();
            _seen.Clear();
        }
    }
}
