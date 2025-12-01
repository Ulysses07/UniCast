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

        public YouTubeChatIngestor(string videoId) : base(videoId)
        {
            _httpClient = new HttpClient
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

            // Video ID'den Live Chat ID'yi al
            var url = $"https://www.googleapis.com/youtube/v3/videos" +
                      $"?part=liveStreamingDetails&id={_identifier}&key={ApiKey}";

            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
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
                    await FetchMessagesAsync(ct);
                    await Task.Delay(_pollingIntervalMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
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

        private async Task FetchMessagesAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(ApiKey))
            {
                // Mock mod - test mesajları oluştur
                await GenerateMockMessagesAsync(ct);
                return;
            }

            var url = $"https://www.googleapis.com/youtube/v3/liveChat/messages" +
                      $"?liveChatId={_liveChatId}&part=snippet,authorDetails&key={ApiKey}";

            if (!string.IsNullOrEmpty(_nextPageToken))
                url += $"&pageToken={_nextPageToken}";

            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
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
}