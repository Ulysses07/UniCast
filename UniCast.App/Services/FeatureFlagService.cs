using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Timer = System.Threading.Timer;

namespace UniCast.App.Services
{
    /// <summary>
    /// Feature Flags Service - Server'dan özellik ayarları çeker.
    /// Enterprise seviye remote configuration sistemi.
    /// Thread-safe, offline-capable, auto-refresh.
    /// </summary>
    public sealed class FeatureFlagService : IDisposable
    {
        #region Singleton

        private static readonly Lazy<FeatureFlagService> _instance =
            new(() => new FeatureFlagService(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static FeatureFlagService Instance => _instance.Value;

        #endregion

        #region Fields

        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<string, FeatureFlag> _flags = new();
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private Timer? _refreshTimer;
        private bool _disposed;

        // Varsayılan flag değerleri (offline/hata durumunda)
        private static readonly Dictionary<string, FeatureFlag> _defaults = new()
        {
            // Streaming özellikleri
            ["multiplatform_streaming"] = new() { Key = "multiplatform_streaming", Enabled = true, Description = "Çoklu platform yayını" },
            ["youtube_chat"] = new() { Key = "youtube_chat", Enabled = true, Description = "YouTube chat entegrasyonu" },
            ["twitch_chat"] = new() { Key = "twitch_chat", Enabled = true, Description = "Twitch chat entegrasyonu" },
            ["facebook_chat"] = new() { Key = "facebook_chat", Enabled = true, Description = "Facebook chat entegrasyonu" },
            ["instagram_chat"] = new() { Key = "instagram_chat", Enabled = true, Description = "Instagram chat entegrasyonu" },
            ["tiktok_chat"] = new() { Key = "tiktok_chat", Enabled = true, Description = "TikTok chat entegrasyonu" },
            
            // Encoder özellikleri
            ["hardware_encoding"] = new() { Key = "hardware_encoding", Enabled = true, Description = "GPU hızlandırmalı encoding" },
            ["nvenc_support"] = new() { Key = "nvenc_support", Enabled = true, Description = "NVIDIA NVENC desteği" },
            ["qsv_support"] = new() { Key = "qsv_support", Enabled = true, Description = "Intel QuickSync desteği" },
            ["hdr_support"] = new() { Key = "hdr_support", Enabled = false, Description = "HDR yayın desteği (beta)" },
            
            // UI özellikleri
            ["dark_mode"] = new() { Key = "dark_mode", Enabled = true, Description = "Karanlık tema" },
            ["chat_overlay"] = new() { Key = "chat_overlay", Enabled = true, Description = "Chat overlay" },
            ["custom_themes"] = new() { Key = "custom_themes", Enabled = false, Description = "Özel temalar (yakında)" },
            
            // Deneysel özellikler
            ["experimental_features"] = new() { Key = "experimental_features", Enabled = false, Description = "Deneysel özellikler" },
            ["beta_updates"] = new() { Key = "beta_updates", Enabled = false, Description = "Beta güncellemeleri al" },
            
            // Analytics
            ["telemetry_enabled"] = new() { Key = "telemetry_enabled", Enabled = true, Description = "Anonim kullanım verileri" },
            ["crash_reporting"] = new() { Key = "crash_reporting", Enabled = true, Description = "Hata raporlama" }
        };

        #endregion

        #region Properties

        /// <summary>
        /// Feature flags sunucu URL'i
        /// </summary>
        public string ServerUrl { get; set; } = "https://license.unicastapp.com/api/v1/features";

        /// <summary>
        /// Otomatik yenileme aralığı (dakika)
        /// </summary>
        public int RefreshIntervalMinutes { get; set; } = 30;

        /// <summary>
        /// Son başarılı yenileme zamanı
        /// </summary>
        public DateTime? LastRefresh { get; private set; }

        /// <summary>
        /// Çevrimdışı mod aktif mi
        /// </summary>
        public bool IsOffline { get; private set; }

        #endregion

        #region Events

        /// <summary>
        /// Flag değiştiğinde tetiklenir
        /// </summary>
        public event EventHandler<FeatureFlagChangedEventArgs>? FlagChanged;

        #endregion

        #region Constructor

        private FeatureFlagService()
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "UniCast-FeatureFlags/1.0");

            // Varsayılanları yükle
            foreach (var kvp in _defaults)
            {
                _flags[kvp.Key] = kvp.Value;
            }

            Log.Debug("[FeatureFlags] Service initialized with {Count} default flags", _defaults.Count);
        }

        #endregion

        #region Public API

        /// <summary>
        /// Servisi başlatır ve ilk flag'leri yükler
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            try
            {
                await RefreshFlagsAsync(ct).ConfigureAwait(false);
                StartAutoRefresh();
                Log.Information("[FeatureFlags] Service started successfully");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[FeatureFlags] Failed to fetch remote flags, using defaults");
                IsOffline = true;
            }
        }

        /// <summary>
        /// Bir özelliğin aktif olup olmadığını kontrol eder
        /// </summary>
        public bool IsEnabled(string flagKey)
        {
            if (_flags.TryGetValue(flagKey, out var flag))
            {
                return flag.Enabled;
            }

            Log.Warning("[FeatureFlags] Unknown flag requested: {Key}", flagKey);
            return false;
        }

        /// <summary>
        /// Bir özelliğin aktif olup olmadığını kontrol eder (varsayılan değer ile)
        /// </summary>
        public bool IsEnabled(string flagKey, bool defaultValue)
        {
            if (_flags.TryGetValue(flagKey, out var flag))
            {
                return flag.Enabled;
            }

            return defaultValue;
        }

        /// <summary>
        /// Flag'in string değerini alır (config değerleri için)
        /// </summary>
        public string GetValue(string flagKey, string defaultValue = "")
        {
            if (_flags.TryGetValue(flagKey, out var flag))
            {
                return flag.Value ?? defaultValue;
            }

            return defaultValue;
        }

        /// <summary>
        /// Flag'in int değerini alır
        /// </summary>
        public int GetIntValue(string flagKey, int defaultValue = 0)
        {
            var value = GetValue(flagKey);
            return int.TryParse(value, out var result) ? result : defaultValue;
        }

        /// <summary>
        /// Tüm flag'leri döndürür
        /// </summary>
        public IReadOnlyDictionary<string, FeatureFlag> GetAllFlags()
        {
            return _flags.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        /// <summary>
        /// Flag'leri manuel olarak yeniler
        /// </summary>
        public async Task RefreshFlagsAsync(CancellationToken ct = default)
        {
            if (!await _refreshLock.WaitAsync(0, ct).ConfigureAwait(false))
            {
                Log.Debug("[FeatureFlags] Refresh already in progress, skipping");
                return;
            }

            try
            {
                Log.Debug("[FeatureFlags] Refreshing flags from server...");

                var response = await _httpClient.GetAsync(ServerUrl, ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var remoteFlags = JsonSerializer.Deserialize<List<FeatureFlag>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (remoteFlags != null)
                    {
                        foreach (var flag in remoteFlags)
                        {
                            var oldEnabled = _flags.TryGetValue(flag.Key, out var old) ? old.Enabled : false;
                            _flags[flag.Key] = flag;

                            // Değişiklik varsa event tetikle
                            if (oldEnabled != flag.Enabled)
                            {
                                FlagChanged?.Invoke(this, new FeatureFlagChangedEventArgs(flag.Key, oldEnabled, flag.Enabled));
                                Log.Information("[FeatureFlags] Flag changed: {Key} = {Value}", flag.Key, flag.Enabled);
                            }
                        }

                        LastRefresh = DateTime.UtcNow;
                        IsOffline = false;
                        Log.Debug("[FeatureFlags] Loaded {Count} flags from server", remoteFlags.Count);
                    }
                }
                else
                {
                    Log.Warning("[FeatureFlags] Server returned {StatusCode}", response.StatusCode);
                    IsOffline = true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[FeatureFlags] Failed to refresh flags");
                IsOffline = true;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        #endregion

        #region Private Methods

        private void StartAutoRefresh()
        {
            _refreshTimer?.Dispose();
            _refreshTimer = new Timer(
                async _ => await RefreshFlagsAsync().ConfigureAwait(false),
                null,
                TimeSpan.FromMinutes(RefreshIntervalMinutes),
                TimeSpan.FromMinutes(RefreshIntervalMinutes)
            );

            Log.Debug("[FeatureFlags] Auto-refresh enabled: every {Minutes} minutes", RefreshIntervalMinutes);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _refreshTimer?.Dispose();
            _refreshLock.Dispose();
            _httpClient.Dispose();

            Log.Debug("[FeatureFlags] Service disposed");
        }

        #endregion
    }

    #region Models

    /// <summary>
    /// Feature flag modeli
    /// </summary>
    public class FeatureFlag
    {
        public string Key { get; set; } = "";
        public bool Enabled { get; set; }
        public string? Value { get; set; }
        public string? Description { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }

    /// <summary>
    /// Flag değişiklik event args
    /// </summary>
    public class FeatureFlagChangedEventArgs : EventArgs
    {
        public string FlagKey { get; }
        public bool OldValue { get; }
        public bool NewValue { get; }

        public FeatureFlagChangedEventArgs(string flagKey, bool oldValue, bool newValue)
        {
            FlagKey = flagKey;
            OldValue = oldValue;
            NewValue = newValue;
        }
    }

    #endregion
}
