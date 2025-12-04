using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace UniCast.LicenseServer.Services
{
    /// <summary>
    /// DÜZELTME v18: HardwareId bazlı rate limiting eklendi.
    /// IP + HardwareId kombinasyonu ile daha güvenli rate limiting.
    /// </summary>
    public class RateLimiter : IDisposable
    {
        private readonly ConcurrentDictionary<string, RateLimitEntry> _entries = new();
        private readonly ConcurrentDictionary<string, HardwareIdTracker> _hardwareTrackers = new();
        private readonly Dictionary<string, RateLimitConfig> _configs;
        private readonly Timer _cleanupTimer;
        private bool _disposed;

        // DÜZELTME v18: HardwareId bazlı global limitler
        private static class HardwareIdLimits
        {
            public const int MaxActivationsPerDay = 10;
            public const int MaxDeactivationsPerDay = 5;
            public const int MaxValidationsPerHour = 100;
            public const int SuspiciousActivityThreshold = 50; // Şüpheli aktivite eşiği
        }

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

        /// <summary>
        /// IP bazlı rate limit kontrolü (mevcut davranış)
        /// </summary>
        public bool TryAcquire(string clientId, string operation)
        {
            return TryAcquire(clientId, operation, out _, out _);
        }

        /// <summary>
        /// IP bazlı rate limit kontrolü (kalan istek ve reset zamanı ile)
        /// </summary>
        public bool TryAcquire(string clientId, string operation, out int remaining, out DateTime resetTime)
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

                // Reset zamanını hesapla
                resetTime = entry.Requests.Count > 0
                    ? entry.Requests.Min() + config.Window
                    : now + config.Window;

                // Kalan istekleri hesapla
                remaining = Math.Max(0, config.MaxRequests - entry.Requests.Count);

                // Limit kontrolü
                if (entry.Requests.Count >= config.MaxRequests)
                {
                    return false;
                }

                entry.Requests.Add(now);
                remaining = Math.Max(0, config.MaxRequests - entry.Requests.Count);
                return true;
            }
        }

        /// <summary>
        /// DÜZELTME v18: HardwareId bazlı rate limit kontrolü
        /// </summary>
        public RateLimitResult TryAcquireWithHardwareId(string clientIp, string hardwareId, string operation)
        {
            // Önce IP bazlı kontrol
            if (!TryAcquire(clientIp, operation))
            {
                return new RateLimitResult
                {
                    Allowed = false,
                    Reason = "IP rate limit exceeded",
                    RetryAfterSeconds = 60
                };
            }

            // HardwareId bazlı kontrol
            var tracker = _hardwareTrackers.GetOrAdd(hardwareId, _ => new HardwareIdTracker());
            var now = DateTime.UtcNow;

            lock (tracker)
            {
                // Günlük/saatlik istatistikleri temizle
                CleanupTracker(tracker, now);

                // Şüpheli aktivite kontrolü
                if (tracker.TotalRequestsToday >= HardwareIdLimits.SuspiciousActivityThreshold)
                {
                    tracker.IsSuspicious = true;
                    return new RateLimitResult
                    {
                        Allowed = false,
                        Reason = "Suspicious activity detected",
                        RetryAfterSeconds = 3600, // 1 saat
                        IsSuspicious = true
                    };
                }

                // Operasyon bazlı limit kontrolü
                var (allowed, reason, retryAfter) = operation switch
                {
                    "activate" => CheckActivationLimit(tracker, now),
                    "deactivate" => CheckDeactivationLimit(tracker, now),
                    "validate" => CheckValidationLimit(tracker, now),
                    _ => (true, null, 0)
                };

                if (!allowed)
                {
                    return new RateLimitResult
                    {
                        Allowed = false,
                        Reason = reason!,
                        RetryAfterSeconds = retryAfter
                    };
                }

                // İsteği kaydet
                RecordRequest(tracker, operation, now);

                return new RateLimitResult
                {
                    Allowed = true,
                    RemainingRequests = GetRemainingForOperation(tracker, operation)
                };
            }
        }

        private (bool allowed, string? reason, int retryAfter) CheckActivationLimit(HardwareIdTracker tracker, DateTime now)
        {
            if (tracker.ActivationsToday >= HardwareIdLimits.MaxActivationsPerDay)
            {
                var nextReset = now.Date.AddDays(1);
                return (false, "Daily activation limit exceeded", (int)(nextReset - now).TotalSeconds);
            }
            return (true, null, 0);
        }

        private (bool allowed, string? reason, int retryAfter) CheckDeactivationLimit(HardwareIdTracker tracker, DateTime now)
        {
            if (tracker.DeactivationsToday >= HardwareIdLimits.MaxDeactivationsPerDay)
            {
                var nextReset = now.Date.AddDays(1);
                return (false, "Daily deactivation limit exceeded", (int)(nextReset - now).TotalSeconds);
            }
            return (true, null, 0);
        }

        private (bool allowed, string? reason, int retryAfter) CheckValidationLimit(HardwareIdTracker tracker, DateTime now)
        {
            if (tracker.ValidationsThisHour >= HardwareIdLimits.MaxValidationsPerHour)
            {
                var nextReset = now.AddHours(1).Date.AddHours(now.Hour + 1);
                return (false, "Hourly validation limit exceeded", (int)(nextReset - now).TotalSeconds);
            }
            return (true, null, 0);
        }

        private void RecordRequest(HardwareIdTracker tracker, string operation, DateTime now)
        {
            tracker.LastRequestTime = now;
            tracker.TotalRequestsToday++;

            switch (operation)
            {
                case "activate":
                    tracker.ActivationsToday++;
                    break;
                case "deactivate":
                    tracker.DeactivationsToday++;
                    break;
                case "validate":
                    tracker.ValidationsThisHour++;
                    break;
            }
        }

        private int GetRemainingForOperation(HardwareIdTracker tracker, string operation)
        {
            return operation switch
            {
                "activate" => HardwareIdLimits.MaxActivationsPerDay - tracker.ActivationsToday,
                "deactivate" => HardwareIdLimits.MaxDeactivationsPerDay - tracker.DeactivationsToday,
                "validate" => HardwareIdLimits.MaxValidationsPerHour - tracker.ValidationsThisHour,
                _ => 100
            };
        }

        private void CleanupTracker(HardwareIdTracker tracker, DateTime now)
        {
            // Günlük reset
            if (tracker.LastResetDate.Date != now.Date)
            {
                tracker.ActivationsToday = 0;
                tracker.DeactivationsToday = 0;
                tracker.TotalRequestsToday = 0;
                tracker.LastResetDate = now;
                tracker.IsSuspicious = false; // Günlük şüpheli bayrak reset
            }

            // Saatlik reset (validation için)
            if (tracker.LastHourlyReset.Hour != now.Hour || tracker.LastHourlyReset.Date != now.Date)
            {
                tracker.ValidationsThisHour = 0;
                tracker.LastHourlyReset = now;
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

        /// <summary>
        /// DÜZELTME v18: HardwareId istatistiklerini getir
        /// </summary>
        public HardwareIdStats? GetHardwareIdStats(string hardwareId)
        {
            if (!_hardwareTrackers.TryGetValue(hardwareId, out var tracker))
                return null;

            lock (tracker)
            {
                return new HardwareIdStats
                {
                    HardwareId = hardwareId,
                    ActivationsToday = tracker.ActivationsToday,
                    DeactivationsToday = tracker.DeactivationsToday,
                    ValidationsThisHour = tracker.ValidationsThisHour,
                    TotalRequestsToday = tracker.TotalRequestsToday,
                    IsSuspicious = tracker.IsSuspicious,
                    LastRequestTime = tracker.LastRequestTime
                };
            }
        }

        /// <summary>
        /// DÜZELTME v18: Şüpheli HardwareId'leri listele
        /// </summary>
        public IEnumerable<string> GetSuspiciousHardwareIds()
        {
            foreach (var kvp in _hardwareTrackers)
            {
                lock (kvp.Value)
                {
                    if (kvp.Value.IsSuspicious)
                        yield return kvp.Key;
                }
            }
        }

        private void Cleanup(object? state)
        {
            var now = DateTime.UtcNow;
            var keysToRemove = new List<string>();

            // IP entries temizliği
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

            // HardwareId entries temizliği (24 saat aktivite yoksa)
            var hardwareKeysToRemove = new List<string>();
            foreach (var kvp in _hardwareTrackers)
            {
                lock (kvp.Value)
                {
                    if ((now - kvp.Value.LastRequestTime).TotalHours > 24)
                    {
                        hardwareKeysToRemove.Add(kvp.Key);
                    }
                }
            }

            foreach (var key in hardwareKeysToRemove)
            {
                _hardwareTrackers.TryRemove(key, out _);
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

    /// <summary>
    /// DÜZELTME v18: HardwareId takip sınıfı
    /// </summary>
    internal class HardwareIdTracker
    {
        public int ActivationsToday { get; set; }
        public int DeactivationsToday { get; set; }
        public int ValidationsThisHour { get; set; }
        public int TotalRequestsToday { get; set; }
        public bool IsSuspicious { get; set; }
        public DateTime LastRequestTime { get; set; } = DateTime.UtcNow;
        public DateTime LastResetDate { get; set; } = DateTime.UtcNow;
        public DateTime LastHourlyReset { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// DÜZELTME v18: Rate limit sonucu
    /// </summary>
    public class RateLimitResult
    {
        public bool Allowed { get; init; }
        public string? Reason { get; init; }
        public int RetryAfterSeconds { get; init; }
        public int RemainingRequests { get; init; }
        public bool IsSuspicious { get; init; }
    }

    /// <summary>
    /// DÜZELTME v18: HardwareId istatistikleri
    /// </summary>
    public class HardwareIdStats
    {
        public string HardwareId { get; init; } = "";
        public int ActivationsToday { get; init; }
        public int DeactivationsToday { get; init; }
        public int ValidationsThisHour { get; init; }
        public int TotalRequestsToday { get; init; }
        public bool IsSuspicious { get; init; }
        public DateTime LastRequestTime { get; init; }
    }
}