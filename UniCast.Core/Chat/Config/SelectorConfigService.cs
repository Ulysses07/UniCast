using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.Core.Chat.Config
{
    /// <summary>
    /// Instagram selector'ları - sunucudan alınır veya varsayılan kullanılır
    /// </summary>
    public class InstagramSelectors
    {
        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("broadcaster")]
        public string? Broadcaster { get; set; }
    }

    /// <summary>
    /// Instagram selector konfigürasyonu
    /// </summary>
    public class InstagramSelectorConfig
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTime UpdatedAt { get; set; }

        [JsonPropertyName("selectors")]
        public InstagramSelectors Selectors { get; set; } = new();

        [JsonPropertyName("fallbackSelectors")]
        public InstagramSelectors? FallbackSelectors { get; set; }

        [JsonPropertyName("pollingIntervalMs")]
        public int PollingIntervalMs { get; set; } = 3000;

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }
    }

    /// <summary>
    /// Facebook selector'ları
    /// </summary>
    public class FacebookSelectors
    {
        [JsonPropertyName("commentContainer")]
        public string? CommentContainer { get; set; }

        [JsonPropertyName("authorLink")]
        public string? AuthorLink { get; set; }

        [JsonPropertyName("commentText")]
        public string? CommentText { get; set; }
    }

    /// <summary>
    /// Tüm selector'lar
    /// </summary>
    public class AllSelectorsConfig
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTime UpdatedAt { get; set; }

        [JsonPropertyName("instagram")]
        public InstagramSelectors? Instagram { get; set; }

        [JsonPropertyName("facebook")]
        public FacebookSelectors? Facebook { get; set; }
    }

    /// <summary>
    /// Selector konfigürasyonlarını sunucudan çeken servis.
    /// Başarısız olursa hardcoded fallback değerleri kullanır.
    /// </summary>
    public class SelectorConfigService
    {
        // Sunucu URL - Production için değiştir
        private const string DefaultServerUrl = "https://license.unicastapp.com";

        // Fallback selector'lar - sunucuya ulaşılamazsa bunlar kullanılır
        private static readonly InstagramSelectors DefaultInstagramSelectors = new()
        {
            Username = "span._ap3a._aaco._aacw._aacx._aad7",
            Message = "span._ap3a._aaco._aacu._aacx._aad7._aadf",
            Broadcaster = "span._ap3a._aaco._aacw._aacx._aada"
        };

        private static readonly FacebookSelectors DefaultFacebookSelectors = new()
        {
            CommentContainer = "div.xv55zj0.x1vvkbs",
            AuthorLink = "a",
            CommentText = "div[dir='auto']"
        };

        private readonly HttpClient _httpClient;
        private readonly string _serverUrl;
        private readonly SemaphoreSlim _lock = new(1, 1);

        // Cache
        private InstagramSelectorConfig? _instagramConfig;
        private DateTime _instagramConfigFetchedAt = DateTime.MinValue;
        private readonly TimeSpan _cacheExpiry = TimeSpan.FromHours(1);

        public SelectorConfigService(string? serverUrl = null)
        {
            _serverUrl = serverUrl ?? DefaultServerUrl;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "UniCast/1.0");
        }

        /// <summary>
        /// Instagram selector'larını getirir.
        /// Önce cache'e, sonra sunucuya, en son fallback'e bakar.
        /// </summary>
        public async Task<InstagramSelectors> GetInstagramSelectorsAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                // Cache geçerli mi?
                if (_instagramConfig != null && DateTime.UtcNow - _instagramConfigFetchedAt < _cacheExpiry)
                {
                    Log.Debug("[SelectorConfig] Instagram selectors cache'den alındı (v{Version})", _instagramConfig.Version);
                    return _instagramConfig.Selectors;
                }

                // Sunucudan çek
                try
                {
                    var url = $"{_serverUrl}/api/v1/config/instagram-selectors";
                    Log.Debug("[SelectorConfig] Instagram selectors sunucudan çekiliyor: {Url}", url);

                    var response = await _httpClient.GetAsync(url, ct);

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync(ct);
                        var config = JsonSerializer.Deserialize<InstagramSelectorConfig>(json);

                        if (config?.Selectors != null)
                        {
                            _instagramConfig = config;
                            _instagramConfigFetchedAt = DateTime.UtcNow;
                            Log.Information("[SelectorConfig] Instagram selectors güncellendi (v{Version}, {Date})",
                                config.Version, config.UpdatedAt);
                            return config.Selectors;
                        }
                    }
                    else
                    {
                        Log.Warning("[SelectorConfig] Sunucu hatası: {Status}", response.StatusCode);
                    }
                }
                catch (HttpRequestException ex)
                {
                    Log.Warning("[SelectorConfig] Sunucuya bağlanılamadı: {Error}", ex.Message);
                }
                catch (TaskCanceledException)
                {
                    Log.Warning("[SelectorConfig] İstek zaman aşımına uğradı");
                }
                catch (JsonException ex)
                {
                    Log.Warning("[SelectorConfig] JSON parse hatası: {Error}", ex.Message);
                }

                // Fallback kullan
                Log.Information("[SelectorConfig] Fallback Instagram selectors kullanılıyor");
                return DefaultInstagramSelectors;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Instagram selector'larını senkron olarak getirir (başlangıç için).
        /// </summary>
        public InstagramSelectors GetInstagramSelectors()
        {
            try
            {
                return GetInstagramSelectorsAsync().GetAwaiter().GetResult();
            }
            catch
            {
                return DefaultInstagramSelectors;
            }
        }

        /// <summary>
        /// Facebook selector'larını getirir.
        /// </summary>
        public FacebookSelectors GetFacebookSelectors()
        {
            // Şimdilik sadece fallback döndür
            // İleride sunucudan çekme eklenebilir
            return DefaultFacebookSelectors;
        }

        /// <summary>
        /// Cache'i temizler ve sonraki çağrıda sunucudan yeniden çeker.
        /// </summary>
        public void InvalidateCache()
        {
            _instagramConfig = null;
            _instagramConfigFetchedAt = DateTime.MinValue;
            Log.Debug("[SelectorConfig] Cache temizlendi");
        }

        /// <summary>
        /// Singleton instance
        /// </summary>
        private static SelectorConfigService? _instance;
        private static readonly object _instanceLock = new();

        public static SelectorConfigService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new SelectorConfigService();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Test veya özel sunucu URL'i için instance oluşturur
        /// </summary>
        public static void Initialize(string serverUrl)
        {
            lock (_instanceLock)
            {
                _instance = new SelectorConfigService(serverUrl);
            }
        }
    }
}