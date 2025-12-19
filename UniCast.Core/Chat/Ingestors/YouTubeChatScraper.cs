using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Serilog;

namespace UniCast.Core.Chat.Ingestors
{
    /// <summary>
    /// YouTube Live Chat Scraper - API key gerektirmez!
    /// YouTube Live Chat sayfasını scrape ederek mesajları alır.
    /// 
    /// Avantajları:
    /// - API key gerektirmez
    /// - Quota limiti yok
    /// - Sınırsız kullanım
    /// 
    /// Dezavantajları:
    /// - YouTube değişiklik yaparsa güncelleme gerekebilir
    /// </summary>
    public sealed class YouTubeChatScraper : BaseChatIngestor
    {
        private readonly HttpClient _httpClient;
        private string? _continuation;
        private string? _apiKey; // YouTube'un kendi internal API key'i (sayfadan alınır)
        private string? _clientVersion;
        private int _pollingIntervalMs = 2000; // 2 saniye varsayılan, max 5 saniye
        private readonly HashSet<string> _seenMessageIds = new();

        public override ChatPlatform Platform => ChatPlatform.YouTube;

        /// <summary>
        /// Video URL veya ID ile oluştur
        /// Kabul edilen formatlar:
        /// - https://www.youtube.com/watch?v=VIDEO_ID
        /// - https://youtu.be/VIDEO_ID
        /// - VIDEO_ID
        /// </summary>
        public YouTubeChatScraper(string videoUrlOrId) : base(ExtractVideoId(videoUrlOrId))
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 5,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            // Browser gibi görünmek için header'lar
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "tr-TR,tr;q=0.9,en-US;q=0.8,en;q=0.7");
            _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        }

        /// <summary>
        /// URL'den Video ID çıkar
        /// </summary>
        private static string ExtractVideoId(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            input = input.Trim();

            // Zaten sadece ID ise
            if (Regex.IsMatch(input, @"^[a-zA-Z0-9_-]{11}$"))
                return input;

            // youtube.com/watch?v=XXX
            var match = Regex.Match(input, @"[?&]v=([a-zA-Z0-9_-]{11})");
            if (match.Success)
                return match.Groups[1].Value;

            // youtu.be/XXX
            match = Regex.Match(input, @"youtu\.be/([a-zA-Z0-9_-]{11})");
            if (match.Success)
                return match.Groups[1].Value;

            // youtube.com/live/XXX
            match = Regex.Match(input, @"youtube\.com/live/([a-zA-Z0-9_-]{11})");
            if (match.Success)
                return match.Groups[1].Value;

            return input;
        }

        protected override async Task ConnectAsync(CancellationToken ct)
        {
            Log.Information("[YouTube Scraper] Bağlanılıyor: {VideoId}", _identifier);

            // Live chat sayfasını al
            var chatUrl = $"https://www.youtube.com/live_chat?v={_identifier}&is_popout=1";

            var response = await _httpClient.GetAsync(chatUrl, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"YouTube'a bağlanılamadı: {response.StatusCode}");
            }

            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // YouTube'un internal API key'ini bul
            var apiKeyMatch = Regex.Match(html, @"""INNERTUBE_API_KEY""\s*:\s*""([^""]+)""");
            if (!apiKeyMatch.Success)
            {
                // Alternatif pattern
                apiKeyMatch = Regex.Match(html, @"innertubeApiKey""\s*:\s*""([^""]+)""");
            }

            if (apiKeyMatch.Success)
            {
                _apiKey = apiKeyMatch.Groups[1].Value;
                Log.Debug("[YouTube Scraper] API Key bulundu");
            }
            else
            {
                throw new Exception("YouTube API key bulunamadı. Sayfa yapısı değişmiş olabilir.");
            }

            // Client version
            var versionMatch = Regex.Match(html, @"""clientVersion""\s*:\s*""([^""]+)""");
            if (versionMatch.Success)
            {
                _clientVersion = versionMatch.Groups[1].Value;
            }
            else
            {
                _clientVersion = "2.20231219.04.00"; // Fallback
            }

            // Continuation token'ı bul
            var continuationMatch = Regex.Match(html, @"""continuation""\s*:\s*""([^""]+)""");
            if (continuationMatch.Success)
            {
                _continuation = continuationMatch.Groups[1].Value;
                Log.Debug("[YouTube Scraper] Continuation token bulundu");
            }
            else
            {
                // ytInitialData içinden bulmayı dene
                var ytDataMatch = Regex.Match(html, @"ytInitialData\s*=\s*(\{.+?\});\s*</script>", RegexOptions.Singleline);
                if (ytDataMatch.Success)
                {
                    try
                    {
                        var jsonData = ytDataMatch.Groups[1].Value;
                        _continuation = ExtractContinuationFromJson(jsonData);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[YouTube Scraper] ytInitialData parse hatası");
                    }
                }
            }

            if (string.IsNullOrEmpty(_continuation))
            {
                throw new Exception("Bu yayında chat bulunamadı veya chat kapalı.");
            }

            Log.Information("[YouTube Scraper] Bağlantı başarılı. Chat dinleniyor...");
        }

        private string? ExtractContinuationFromJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Derin arama yap
                return FindContinuation(root);
            }
            catch
            {
                return null;
            }
        }

        private string? FindContinuation(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("continuation", out var cont))
                {
                    return cont.GetString();
                }

                foreach (var prop in element.EnumerateObject())
                {
                    var result = FindContinuation(prop.Value);
                    if (result != null) return result;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    var result = FindContinuation(item);
                    if (result != null) return result;
                }
            }

            return null;
        }

        protected override Task DisconnectAsync()
        {
            _continuation = null;
            _apiKey = null;
            _seenMessageIds.Clear();
            return Task.CompletedTask;
        }

        protected override async Task RunMessageLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && State == ConnectionState.Connected)
            {
                try
                {
                    await FetchMessagesAsync(ct);
                    await Task.Delay(_pollingIntervalMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[YouTube Scraper] Mesaj alma hatası, yeniden deneniyor...");
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                }
            }
        }

        private async Task FetchMessagesAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_continuation) || string.IsNullOrEmpty(_apiKey))
            {
                return;
            }

            var url = $"https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key={_apiKey}";

            var requestBody = new
            {
                context = new
                {
                    client = new
                    {
                        clientName = "WEB",
                        clientVersion = _clientVersion,
                        hl = "tr",
                        gl = "TR",
                        timeZone = "Europe/Istanbul"
                    }
                },
                continuation = _continuation
            };

            var jsonContent = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("[YouTube Scraper] API isteği başarısız: {Status}", response.StatusCode);
                return;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Yeni continuation token'ı al
                if (TryGetNestedProperty(root, "continuationContents.liveChatContinuation.continuations", out var continuations))
                {
                    foreach (var cont in continuations.EnumerateArray())
                    {
                        if (cont.TryGetProperty("invalidationContinuationData", out var invalidation))
                        {
                            if (invalidation.TryGetProperty("continuation", out var contToken))
                            {
                                _continuation = contToken.GetString();
                            }
                            if (invalidation.TryGetProperty("timeoutMs", out var timeout))
                            {
                                // YouTube'un önerdiği değeri kullan ama max 5 saniye
                                _pollingIntervalMs = Math.Clamp(timeout.GetInt32(), 1000, 5000);
                            }
                        }
                        else if (cont.TryGetProperty("timedContinuationData", out var timed))
                        {
                            if (timed.TryGetProperty("continuation", out var contToken))
                            {
                                _continuation = contToken.GetString();
                            }
                            if (timed.TryGetProperty("timeoutMs", out var timeout))
                            {
                                // YouTube'un önerdiği değeri kullan ama max 5 saniye
                                _pollingIntervalMs = Math.Clamp(timeout.GetInt32(), 1000, 5000);
                            }
                        }
                    }
                }

                // Mesajları al
                if (TryGetNestedProperty(root, "continuationContents.liveChatContinuation.actions", out var actions))
                {
                    var actionCount = 0;
                    foreach (var action in actions.EnumerateArray())
                    {
                        actionCount++;
                        ProcessAction(action);
                    }
                    if (actionCount > 0)
                    {
                        Log.Debug("[YouTube Scraper] {Count} action işlendi", actionCount);
                    }
                }
                else
                {
                    Log.Debug("[YouTube Scraper] Yeni mesaj yok (actions bulunamadı)");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[YouTube Scraper] JSON parse hatası");
            }
        }

        private void ProcessAction(JsonElement action)
        {
            try
            {
                if (!action.TryGetProperty("replayChatItemAction", out var replayAction) &&
                    !action.TryGetProperty("addChatItemAction", out var addAction))
                {
                    // Action tipi ne olduğunu logla
                    var props = string.Join(", ", action.EnumerateObject().Select(p => p.Name));
                    Log.Debug("[YouTube Scraper] Bilinmeyen action tipi: {Props}", props);
                    return;
                }

                var itemAction = action.TryGetProperty("replayChatItemAction", out var replay)
                    ? replay
                    : action.GetProperty("addChatItemAction");

                if (!itemAction.TryGetProperty("item", out var item))
                {
                    return;
                }

                // Normal mesajlar
                if (item.TryGetProperty("liveChatTextMessageRenderer", out var textMessage))
                {
                    var message = ParseTextMessage(textMessage);
                    if (message != null && !_seenMessageIds.Contains(message.Username + message.Timestamp.Ticks))
                    {
                        _seenMessageIds.Add(message.Username + message.Timestamp.Ticks);
                        Log.Debug("[YouTube Scraper] Mesaj alındı: {User}: {Msg}", message.Username, message.Message);
                        PublishMessage(message);
                    }
                }
                // Super Chat
                else if (item.TryGetProperty("liveChatPaidMessageRenderer", out var paidMessage))
                {
                    var message = ParsePaidMessage(paidMessage);
                    if (message != null && !_seenMessageIds.Contains(message.Username + message.Timestamp.Ticks))
                    {
                        _seenMessageIds.Add(message.Username + message.Timestamp.Ticks);
                        PublishMessage(message);
                    }
                }
                // Super Sticker
                else if (item.TryGetProperty("liveChatPaidStickerRenderer", out var sticker))
                {
                    var message = ParseStickerMessage(sticker);
                    if (message != null && !_seenMessageIds.Contains(message.Username + message.Timestamp.Ticks))
                    {
                        _seenMessageIds.Add(message.Username + message.Timestamp.Ticks);
                        PublishMessage(message);
                    }
                }
                // Membership
                else if (item.TryGetProperty("liveChatMembershipItemRenderer", out var membership))
                {
                    var message = ParseMembershipMessage(membership);
                    if (message != null && !_seenMessageIds.Contains(message.Username + message.Timestamp.Ticks))
                    {
                        _seenMessageIds.Add(message.Username + message.Timestamp.Ticks);
                        PublishMessage(message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[YouTube Scraper] Action parse hatası");
            }
        }

        private ChatMessage? ParseTextMessage(JsonElement renderer)
        {
            try
            {
                var messageText = GetMessageText(renderer);
                var authorName = GetRunsText(renderer, "authorName");
                var authorChannelId = renderer.TryGetProperty("authorExternalChannelId", out var channelId)
                    ? channelId.GetString() ?? ""
                    : "";

                var avatarUrl = GetThumbnailUrl(renderer, "authorPhoto");

                var isModerator = renderer.TryGetProperty("authorBadges", out var badges) &&
                    badges.ToString().Contains("MODERATOR");
                var isOwner = badges.ToString().Contains("OWNER");
                var isMember = badges.ToString().Contains("MEMBER");

                var timestamp = DateTime.UtcNow;
                if (renderer.TryGetProperty("timestampUsec", out var ts))
                {
                    var usec = long.Parse(ts.GetString() ?? "0");
                    timestamp = DateTimeOffset.FromUnixTimeMilliseconds(usec / 1000).UtcDateTime;
                }

                return new ChatMessage
                {
                    Platform = ChatPlatform.YouTube,
                    Username = authorChannelId,
                    DisplayName = authorName,
                    Message = messageText,
                    AvatarUrl = avatarUrl,
                    IsModerator = isModerator,
                    IsOwner = isOwner,
                    IsSubscriber = isMember,
                    Type = ChatMessageType.Normal,
                    Timestamp = timestamp
                };
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[YouTube Scraper] Text message parse hatası");
                return null;
            }
        }

        private ChatMessage? ParsePaidMessage(JsonElement renderer)
        {
            try
            {
                var message = ParseTextMessage(renderer);
                if (message == null) return null;

                var amount = renderer.TryGetProperty("purchaseAmountText", out var amountText)
                    ? GetSimpleText(amountText)
                    : "";

                return new ChatMessage
                {
                    Platform = message.Platform,
                    Username = message.Username,
                    DisplayName = message.DisplayName,
                    Message = message.Message,
                    AvatarUrl = message.AvatarUrl,
                    IsModerator = message.IsModerator,
                    IsOwner = message.IsOwner,
                    IsSubscriber = message.IsSubscriber,
                    Type = ChatMessageType.Superchat,
                    Timestamp = message.Timestamp,
                    DonationAmount = amount
                };
            }
            catch
            {
                return null;
            }
        }

        private ChatMessage? ParseStickerMessage(JsonElement renderer)
        {
            try
            {
                var authorName = GetRunsText(renderer, "authorName");
                var authorChannelId = renderer.TryGetProperty("authorExternalChannelId", out var channelId)
                    ? channelId.GetString() ?? ""
                    : "";
                var avatarUrl = GetThumbnailUrl(renderer, "authorPhoto");
                var amount = renderer.TryGetProperty("purchaseAmountText", out var amountText)
                    ? GetSimpleText(amountText)
                    : "";

                return new ChatMessage
                {
                    Platform = ChatPlatform.YouTube,
                    Username = authorChannelId,
                    DisplayName = authorName,
                    Message = $"🎟️ Super Sticker gönderdi!",
                    AvatarUrl = avatarUrl,
                    Type = ChatMessageType.Superchat,
                    DonationAmount = amount,
                    Timestamp = DateTime.UtcNow
                };
            }
            catch
            {
                return null;
            }
        }

        private ChatMessage? ParseMembershipMessage(JsonElement renderer)
        {
            try
            {
                var authorName = GetRunsText(renderer, "authorName");
                var authorChannelId = renderer.TryGetProperty("authorExternalChannelId", out var channelId)
                    ? channelId.GetString() ?? ""
                    : "";
                var avatarUrl = GetThumbnailUrl(renderer, "authorPhoto");

                var headerText = renderer.TryGetProperty("headerSubtext", out var subtext)
                    ? GetSimpleText(subtext)
                    : "Üye oldu!";

                return new ChatMessage
                {
                    Platform = ChatPlatform.YouTube,
                    Username = authorChannelId,
                    DisplayName = authorName,
                    Message = $"⭐ {headerText}",
                    AvatarUrl = avatarUrl,
                    Type = ChatMessageType.Subscription,
                    IsSubscriber = true,
                    Timestamp = DateTime.UtcNow
                };
            }
            catch
            {
                return null;
            }
        }

        #region JSON Helper Methods

        private string GetMessageText(JsonElement renderer)
        {
            if (!renderer.TryGetProperty("message", out var message))
                return "";

            return GetRunsText(message);
        }

        private string GetRunsText(JsonElement element, string? propertyName = null)
        {
            var target = element;
            if (propertyName != null && element.TryGetProperty(propertyName, out var prop))
            {
                target = prop;
            }

            if (target.TryGetProperty("simpleText", out var simple))
            {
                return simple.GetString() ?? "";
            }

            if (target.TryGetProperty("runs", out var runs))
            {
                var text = "";
                foreach (var run in runs.EnumerateArray())
                {
                    if (run.TryGetProperty("text", out var t))
                    {
                        text += t.GetString();
                    }
                    else if (run.TryGetProperty("emoji", out var emoji))
                    {
                        if (emoji.TryGetProperty("shortcuts", out var shortcuts) &&
                            shortcuts.GetArrayLength() > 0)
                        {
                            text += shortcuts[0].GetString();
                        }
                    }
                }
                return text;
            }

            return "";
        }

        private string GetSimpleText(JsonElement element)
        {
            if (element.TryGetProperty("simpleText", out var simple))
            {
                return simple.GetString() ?? "";
            }
            return GetRunsText(element);
        }

        private string? GetThumbnailUrl(JsonElement renderer, string propertyName)
        {
            if (renderer.TryGetProperty(propertyName, out var photo) &&
                photo.TryGetProperty("thumbnails", out var thumbnails) &&
                thumbnails.GetArrayLength() > 0)
            {
                return thumbnails[0].GetProperty("url").GetString();
            }
            return null;
        }

        private bool TryGetNestedProperty(JsonElement element, string path, out JsonElement result)
        {
            result = element;
            var parts = path.Split('.');

            foreach (var part in parts)
            {
                if (!result.TryGetProperty(part, out result))
                {
                    return false;
                }
            }

            return true;
        }

        #endregion

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _httpClient.Dispose();
                _seenMessageIds.Clear();
            }
            base.Dispose(disposing);
        }
    }
}