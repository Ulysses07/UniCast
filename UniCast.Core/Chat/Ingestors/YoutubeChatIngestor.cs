using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.Core.Chat.Ingestors
{
    /// <summary>
    /// YouTube Live Chat ingestor.
    /// YouTube Data API v3 kullanır.
    /// 
    /// DÜZELTME v18: Quota tracking eklendi.
    /// - Günlük quota kullanımı takibi
    /// - Quota aşım uyarısı
    /// - Akıllı polling aralığı
    /// </summary>
    public sealed class YouTubeChatIngestor : BaseChatIngestor
    {
        private readonly HttpClient _httpClient;
        private string? _liveChatId;
        private string? _nextPageToken;
        private int _pollingIntervalMs = 5000;

        public override ChatPlatform Platform => ChatPlatform.YouTube;

        /// <summary>
        /// YouTube API Key (ortam değişkeninden veya ayarlardan alınmalı).
        /// </summary>
        public string? ApiKey { get; set; }

        #region DÜZELTME v18: Quota Tracking

        // YouTube API Quota Costs:
        // - videos.list: 1 unit
        // - liveChat/messages.list: 5 units (100 per page)
        // Daily quota: 10,000 units (varsayılan)

        private static class QuotaCosts
        {
            public const int VideosListCost = 1;
            public const int LiveChatMessagesCost = 5;
            public const int DailyLimit = 10000;
            public const int WarningThreshold = 8000; // %80
            public const int CriticalThreshold = 9500; // %95
        }

        private static readonly object _quotaLock = new();
        private static int _dailyQuotaUsed = 0;
        private static DateTime _quotaResetDate = DateTime.UtcNow.Date;

        /// <summary>
        /// Günlük quota kullanımı
        /// </summary>
        public static int DailyQuotaUsed
        {
            get
            {
                lock (_quotaLock)
                {
                    ResetQuotaIfNewDay();
                    return _dailyQuotaUsed;
                }
            }
        }

        /// <summary>
        /// Kalan quota
        /// </summary>
        public static int RemainingQuota => QuotaCosts.DailyLimit - DailyQuotaUsed;

        /// <summary>
        /// Quota yüzdesi
        /// </summary>
        public static double QuotaPercentage => (double)DailyQuotaUsed / QuotaCosts.DailyLimit * 100;

        /// <summary>
        /// Quota durumu
        /// </summary>
        public static QuotaStatus CurrentQuotaStatus
        {
            get
            {
                var used = DailyQuotaUsed;
                if (used >= QuotaCosts.DailyLimit)
                    return QuotaStatus.Exhausted;
                if (used >= QuotaCosts.CriticalThreshold)
                    return QuotaStatus.Critical;
                if (used >= QuotaCosts.WarningThreshold)
                    return QuotaStatus.Warning;
                return QuotaStatus.Normal;
            }
        }

        /// <summary>
        /// Quota değişikliğinde tetiklenen event
        /// </summary>
        public static event EventHandler<QuotaChangedEventArgs>? OnQuotaChanged;

        private static void ResetQuotaIfNewDay()
        {
            var today = DateTime.UtcNow.Date;
            if (_quotaResetDate != today)
            {
                var oldQuota = _dailyQuotaUsed;
                _dailyQuotaUsed = 0;
                _quotaResetDate = today;

                Log.Information("[YouTube] Günlük quota sıfırlandı. Önceki kullanım: {OldQuota}", oldQuota);
            }
        }

        private static void AddQuotaUsage(int cost)
        {
            lock (_quotaLock)
            {
                ResetQuotaIfNewDay();

                var oldStatus = CurrentQuotaStatus;
                _dailyQuotaUsed += cost;
                var newStatus = CurrentQuotaStatus;

                // Status değiştiyse event tetikle
                if (oldStatus != newStatus)
                {
                    Log.Warning("[YouTube] Quota durumu değişti: {Old} -> {New} ({Used}/{Limit})",
                        oldStatus, newStatus, _dailyQuotaUsed, QuotaCosts.DailyLimit);

                    OnQuotaChanged?.Invoke(null, new QuotaChangedEventArgs
                    {
                        OldStatus = oldStatus,
                        NewStatus = newStatus,
                        QuotaUsed = _dailyQuotaUsed,
                        QuotaLimit = QuotaCosts.DailyLimit
                    });
                }
            }
        }

        /// <summary>
        /// Quota bilgilerini döndürür
        /// </summary>
        public static QuotaInfo GetQuotaInfo()
        {
            return new QuotaInfo
            {
                Used = DailyQuotaUsed,
                Limit = QuotaCosts.DailyLimit,
                Remaining = RemainingQuota,
                Percentage = QuotaPercentage,
                Status = CurrentQuotaStatus,
                ResetTime = DateTime.UtcNow.Date.AddDays(1) // Pasifik saatine göre (Google)
            };
        }

        #endregion

        public YouTubeChatIngestor(string videoId) : base(videoId)
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 5,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        protected override async Task ConnectAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(ApiKey))
            {
                // API key yoksa mock modda çalış
                Log.Warning("[YouTube] API Key bulunamadı, mock modda çalışılacak");
                _liveChatId = $"mock_{_identifier}";
                return;
            }

            // DÜZELTME v18: Quota kontrolü
            if (CurrentQuotaStatus == QuotaStatus.Exhausted)
            {
                throw new QuotaExhaustedException("YouTube API günlük quota limiti aşıldı. Yarın tekrar deneyin.");
            }

            // Video ID'den Live Chat ID'yi al
            var url = $"https://www.googleapis.com/youtube/v3/videos" +
                      $"?part=liveStreamingDetails&id={_identifier}&key={ApiKey}";

            var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // DÜZELTME v18: Quota kullanımını kaydet
            AddQuotaUsage(QuotaCosts.VideosListCost);

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);

            var items = doc.RootElement.GetProperty("items");
            if (items.GetArrayLength() == 0)
                throw new Exception("Video bulunamadı");

            var liveChatId = items[0]
                .GetProperty("liveStreamingDetails")
                .GetProperty("activeLiveChatId")
                .GetString();

            if (string.IsNullOrEmpty(liveChatId))
                throw new Exception("Bu video canlı yayın değil veya chat aktif değil");

            _liveChatId = liveChatId;
            Log.Information("[YouTube] Live Chat ID: {ChatId}", _liveChatId);
        }

        protected override Task DisconnectAsync()
        {
            _liveChatId = null;
            _nextPageToken = null;
            return Task.CompletedTask;
        }

        protected override async Task RunMessageLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // DÜZELTME v18: Quota durumuna göre polling aralığını ayarla
                    AdjustPollingInterval();

                    await FetchMessagesAsync(ct);
                    await Task.Delay(_pollingIntervalMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (QuotaExhaustedException ex)
                {
                    Log.Error("[YouTube] {Message}", ex.Message);
                    // Quota bitince 1 saat bekle
                    await Task.Delay(TimeSpan.FromHours(1), ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    Log.Warning(ex, "[YouTube] HTTP hatası, yeniden denenecek");
                    await Task.Delay(10000, ct); // 10 saniye bekle
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[YouTube] Mesaj alma hatası");

                    try
                    {
                        await ReconnectAsync(ct);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// DÜZELTME v18: Quota durumuna göre polling aralığını ayarla
        /// </summary>
        private void AdjustPollingInterval()
        {
            var status = CurrentQuotaStatus;

            _pollingIntervalMs = status switch
            {
                QuotaStatus.Normal => Math.Max(_pollingIntervalMs, 5000),      // Normal: 5+ sn
                QuotaStatus.Warning => Math.Max(_pollingIntervalMs, 10000),    // Uyarı: 10+ sn
                QuotaStatus.Critical => Math.Max(_pollingIntervalMs, 30000),   // Kritik: 30+ sn
                QuotaStatus.Exhausted => 60000,                                 // Bitti: 1 dk (check only)
                _ => _pollingIntervalMs
            };
        }

        private async Task FetchMessagesAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(ApiKey))
            {
                // Mock mod - test mesajları oluştur
                await GenerateMockMessagesAsync(ct);
                return;
            }

            // DÜZELTME v18: Quota kontrolü
            if (CurrentQuotaStatus == QuotaStatus.Exhausted)
            {
                Log.Warning("[YouTube] Quota bitmiş, mesajlar alınamıyor");
                return;
            }

            var url = $"https://www.googleapis.com/youtube/v3/liveChat/messages" +
                      $"?liveChatId={_liveChatId}&part=snippet,authorDetails&key={ApiKey}";

            if (!string.IsNullOrEmpty(_nextPageToken))
                url += $"&pageToken={_nextPageToken}";

            var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // DÜZELTME v18: Quota kullanımını kaydet
            AddQuotaUsage(QuotaCosts.LiveChatMessagesCost);

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);

            // Polling interval'ı güncelle
            if (doc.RootElement.TryGetProperty("pollingIntervalMillis", out var interval))
            {
                _pollingIntervalMs = interval.GetInt32();
            }

            // Next page token'ı güncelle
            if (doc.RootElement.TryGetProperty("nextPageToken", out var token))
            {
                _nextPageToken = token.GetString();
            }

            // Mesajları işle
            var items = doc.RootElement.GetProperty("items");
            foreach (var item in items.EnumerateArray())
            {
                var message = ParseMessage(item);
                if (message != null)
                {
                    PublishMessage(message);
                }
            }
        }

        private ChatMessage? ParseMessage(JsonElement item)
        {
            try
            {
                var snippet = item.GetProperty("snippet");
                var author = item.GetProperty("authorDetails");

                var messageType = snippet.GetProperty("type").GetString();

                // Sadece metin mesajlarını işle (şimdilik)
                if (messageType != "textMessageEvent" && messageType != "superChatEvent")
                    return null;

                var message = new ChatMessage
                {
                    Platform = ChatPlatform.YouTube,
                    Username = author.GetProperty("channelId").GetString() ?? "",
                    DisplayName = author.GetProperty("displayName").GetString() ?? "",
                    Message = snippet.TryGetProperty("displayMessage", out var msg)
                        ? msg.GetString() ?? ""
                        : snippet.GetProperty("textMessageDetails").GetProperty("messageText").GetString() ?? "",
                    AvatarUrl = author.GetProperty("profileImageUrl").GetString(),
                    IsSubscriber = author.GetProperty("isChatSponsor").GetBoolean(),
                    IsModerator = author.GetProperty("isChatModerator").GetBoolean(),
                    IsOwner = author.GetProperty("isChatOwner").GetBoolean(),
                    IsVerified = author.GetProperty("isVerified").GetBoolean(),
                    Type = messageType == "superChatEvent" ? ChatMessageType.Superchat : ChatMessageType.Normal,
                    Timestamp = DateTime.Parse(snippet.GetProperty("publishedAt").GetString() ?? DateTime.UtcNow.ToString())
                };

                // Superchat detayları
                if (messageType == "superChatEvent" && snippet.TryGetProperty("superChatDetails", out var superChat))
                {
                    message = new ChatMessage
                    {
                        Platform = message.Platform,
                        Username = message.Username,
                        DisplayName = message.DisplayName,
                        Message = message.Message,
                        AvatarUrl = message.AvatarUrl,
                        IsSubscriber = message.IsSubscriber,
                        IsModerator = message.IsModerator,
                        IsOwner = message.IsOwner,
                        IsVerified = message.IsVerified,
                        Type = message.Type,
                        Timestamp = message.Timestamp,
                        DonationAmount = superChat.GetProperty("amountDisplayString").GetString(),
                        DonationCurrency = superChat.GetProperty("currency").GetString(),
                        BadgeUrl = message.BadgeUrl,
                        Metadata = message.Metadata
                    };
                }

                return message;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[YouTube] Mesaj parse hatası");
                return null;
            }
        }

        private async Task GenerateMockMessagesAsync(CancellationToken ct)
        {
            // Test için mock mesajlar
            var random = new Random();
            var usernames = new[] { "TestUser1", "TestUser2", "ModUser", "SubUser" };
            var messages = new[] { "Merhaba!", "Harika yayın!", "👍", "Nasılsınız?", "Selam herkese" };

            if (random.Next(100) < 30) // %30 şans
            {
                var mockMessage = new ChatMessage
                {
                    Platform = ChatPlatform.YouTube,
                    Username = $"user_{random.Next(1000)}",
                    DisplayName = usernames[random.Next(usernames.Length)],
                    Message = messages[random.Next(messages.Length)],
                    IsSubscriber = random.Next(100) < 20,
                    IsModerator = random.Next(100) < 5,
                    Timestamp = DateTime.UtcNow
                };

                PublishMessage(mockMessage);
            }

            await Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _httpClient.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    #region DÜZELTME v18: Quota Types

    /// <summary>
    /// Quota durumu
    /// </summary>
    public enum QuotaStatus
    {
        Normal,     // < 80%
        Warning,    // 80-95%
        Critical,   // 95-100%
        Exhausted   // 100%
    }

    /// <summary>
    /// Quota bilgileri
    /// </summary>
    public class QuotaInfo
    {
        public int Used { get; init; }
        public int Limit { get; init; }
        public int Remaining { get; init; }
        public double Percentage { get; init; }
        public QuotaStatus Status { get; init; }
        public DateTime ResetTime { get; init; }
    }

    /// <summary>
    /// Quota değişiklik event argümanları
    /// </summary>
    public class QuotaChangedEventArgs : EventArgs
    {
        public QuotaStatus OldStatus { get; init; }
        public QuotaStatus NewStatus { get; init; }
        public int QuotaUsed { get; init; }
        public int QuotaLimit { get; init; }
    }

    /// <summary>
    /// Quota bittiğinde fırlatılan exception
    /// </summary>
    public class QuotaExhaustedException : Exception
    {
        public QuotaExhaustedException(string message) : base(message) { }
    }

    #endregion
}
