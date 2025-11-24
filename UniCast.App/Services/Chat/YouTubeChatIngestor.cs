using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Core.Chat;
using UniCast.App.Services; // SettingsStore
using UniCast.Core.Settings;

namespace UniCast.App.Services.Chat
{
    public sealed class YouTubeChatIngestor : PollingChatIngestorBase
    {
        private readonly HttpClient _http;

        // State (Durum) Değişkenleri
        private string _apiKey = "";
        private string _liveChatId = "";
        private string? _pageToken;

        public override string Name => "YouTubeChat";

        public YouTubeChatIngestor(HttpClient? http = null)
        {
            _http = http ?? new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        // 1. Ayar Kontrolü
        protected override void ValidateSettings()
        {
            var s = SettingsStore.Load();
            if (string.IsNullOrWhiteSpace(s.YouTubeApiKey) || string.IsNullOrWhiteSpace(s.YouTubeChannelId))
                throw new InvalidOperationException("YouTube API anahtarı veya Kanal ID eksik.");

            // Ayarları state'e alalım (Null-safe)
            _apiKey = s.YouTubeApiKey ?? "";
        }

        // 2. Hazırlık (Video ID ve Chat ID bulma)
        protected override async Task InitializeAsync(CancellationToken ct)
        {
            var s = SettingsStore.Load();
            var channelId = s.YouTubeChannelId ?? "";

            // Video ID bul
            var videoId = await GetActiveLiveVideoIdAsync(_apiKey, channelId, ct);
            if (string.IsNullOrEmpty(videoId))
            {
                throw new Exception("Aktif canlı yayın bulunamadı.");
            }

            // Chat ID bul
            var chatId = await GetActiveLiveChatIdAsync(_apiKey, videoId, ct);
            if (string.IsNullOrEmpty(chatId))
            {
                throw new Exception("Canlı yayın sohbet ID'si alınamadı.");
            }

            _liveChatId = chatId;
            _pageToken = null; // Token'ı sıfırla
        }

        // 3. Veri Çekme (Tek seferlik işlem)
        protected override async Task<(IEnumerable<ChatMessage> messages, int? nextDelayMs)> FetchMessagesAsync(CancellationToken ct)
        {
            // Helper metodu çağır
            var (messages, nextToken, pollMs) = await ListChatMessagesAsync(_apiKey, _liveChatId, _pageToken, ct);

            // Sayfa token'ını güncelle
            _pageToken = nextToken;

            return (messages, pollMs);
        }

        // --- HELPER METOTLAR (API Çağrıları - Aynı Kaldı) ---
        // Sadece private static veya mevcut haliyle kalabilirler.

        private async Task<string?> GetActiveLiveVideoIdAsync(string apiKey, string channelId, CancellationToken ct)
        {
            var url = $"https://www.googleapis.com/youtube/v3/search?part=id&channelId={Uri.EscapeDataString(channelId)}&eventType=live&type=video&key={Uri.EscapeDataString(apiKey)}";
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.TryGetProperty("videoId", out var vid)) return vid.GetString();
            }
            return null;
        }

        private async Task<string?> GetActiveLiveChatIdAsync(string apiKey, string videoId, CancellationToken ct)
        {
            var url = $"https://www.googleapis.com/youtube/v3/videos?part=liveStreamingDetails&id={Uri.EscapeDataString(videoId)}&key={Uri.EscapeDataString(apiKey)}";
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (item.TryGetProperty("liveStreamingDetails", out var lsd) && lsd.TryGetProperty("activeLiveChatId", out var cid)) return cid.GetString();
            }
            return null;
        }

        private async Task<(List<ChatMessage> messages, string? nextPageToken, int pollingMs)> ListChatMessagesAsync(string apiKey, string liveChatId, string? pageToken, CancellationToken ct)
        {
            var url = $"https://www.googleapis.com/youtube/v3/liveChatMessages?part=snippet,authorDetails&liveChatId={Uri.EscapeDataString(liveChatId)}&maxResults=200&key={Uri.EscapeDataString(apiKey)}" + (string.IsNullOrEmpty(pageToken) ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

            var result = new List<ChatMessage>();
            var root = doc.RootElement;
            var pollMs = root.TryGetProperty("pollingIntervalMillis", out var p) ? p.GetInt32() : 1000;
            var next = root.TryGetProperty("nextPageToken", out var npt) ? npt.GetString() : null;

            if (root.TryGetProperty("items", out var items))
            {
                foreach (var it in items.EnumerateArray())
                {
                    string id = it.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                    string author = "Unknown";
                    if (it.TryGetProperty("authorDetails", out var ad) && ad.TryGetProperty("displayName", out var dn)) author = dn.GetString() ?? author;
                    string text = "";
                    DateTimeOffset ts = DateTimeOffset.UtcNow;
                    if (it.TryGetProperty("snippet", out var sn))
                    {
                        if (sn.TryGetProperty("displayMessage", out var dm)) text = dm.GetString() ?? "";
                        if (sn.TryGetProperty("publishedAt", out var pub) && DateTimeOffset.TryParse(pub.GetString(), out var t)) ts = t;
                    }
                    if (!string.IsNullOrWhiteSpace(text))
                        result.Add(new ChatMessage(id, ChatSource.YouTube, ts, author, text));
                }
            }
            return (result, next, pollMs);
        }
    }
}