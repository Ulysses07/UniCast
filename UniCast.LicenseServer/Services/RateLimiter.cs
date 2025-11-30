using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace UniCast.LicenseServer.Services
{
    /// <summary>
    /// Basit rate limiter
    /// </summary>
    public class RateLimiter : IDisposable
    {
        private readonly ConcurrentDictionary<string, RateLimitEntry> _entries = new();
        private readonly Dictionary<string, RateLimitConfig> _configs;
        private readonly Timer _cleanupTimer;
        private bool _disposed;

        public RateLimiter()
        {
            _configs = new Dictionary<string, RateLimitConfig>
            {
                ["activate"] = new(5, TimeSpan.FromMinutes(1)),
                ["deactivate"] = new(3, TimeSpan.FromMinutes(1)),
                ["validate"] = new(30, TimeSpan.FromMinutes(1)),
                ["default"] = new(60, TimeSpan.FromMinutes(1))
            };

            // Her 5 dakikada temizlik
            _cleanupTimer = new Timer(Cleanup, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        public bool TryAcquire(string clientId, string operation)
        {
            var config = _configs.GetValueOrDefault(operation, _configs["default"]);
            var key = $"{clientId}:{operation}";
            var now = DateTime.UtcNow;

            var entry = _entries.GetOrAdd(key, _ => new RateLimitEntry());

            lock (entry)
            {
                // Eski istekleri temizle
                var cutoff = now - config.Window;
                entry.Requests.RemoveAll(t => t < cutoff);

                // Limit kontrolü
                if (entry.Requests.Count >= config.MaxRequests)
                {
                    return false;
                }

                entry.Requests.Add(now);
                return true;
            }
        }

        public int GetRemainingRequests(string clientId, string operation)
        {
            var config = _configs.GetValueOrDefault(operation, _configs["default"]);
            var key = $"{clientId}:{operation}";

            if (!_entries.TryGetValue(key, out var entry))
            {
                return config.MaxRequests;
            }

            lock (entry)
            {
                var cutoff = DateTime.UtcNow - config.Window;
                var activeCount = entry.Requests.Count(t => t >= cutoff);
                return Math.Max(0, config.MaxRequests - activeCount);
            }
        }

        private void Cleanup(object? state)
        {
            var now = DateTime.UtcNow;
            var keysToRemove = new List<string>();

            foreach (var kvp in _entries)
            {
                lock (kvp.Value)
                {
                    kvp.Value.Requests.RemoveAll(t => (now - t).TotalMinutes > 10);

                    if (kvp.Value.Requests.Count == 0)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
            }

            foreach (var key in keysToRemove)
            {
                _entries.TryRemove(key, out _);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cleanupTimer.Dispose();
        }
    }

    internal class RateLimitEntry
    {
        public List<DateTime> Requests { get; } = new();
    }

    internal record RateLimitConfig(int MaxRequests, TimeSpan Window);
}