using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Core.Chat;
using UniCast.App.Services; // SettingsStore
using UniCast.Core.Settings;

namespace UniCast.App.Services.Chat
{
    /// <summary>
    /// YouTube Live Chat okuma (public). Kimlik: API key + ChannelId.
    /// Akış: channelId -> search.live -> videoId -> videos.list.liveStreamingDetails.activeLiveChatId -> liveChatMessages.list (poll).
    /// </summary>
    public sealed class YouTubeChatIngestor : IChatIngestor
    {
        private readonly HttpClient _http;
        private CancellationTokenSource? _cts;
        private Task? _runner;

        public event Action<ChatMessage>? OnMessage;
        public string Name => "YouTubeChat";
        public bool IsRunning { get; private set; }

        public YouTubeChatIngestor(HttpClient? http = null)
        {
            _http = http ?? new HttpClient();
            // Sıkı TLS vb. için default HttpClient yeterli
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        public async Task StartAsync(CancellationToken ct)
        {
            if (IsRunning) return;

            var s = SettingsStore.Load();
            if (string.IsNullOrWhiteSpace(s.YouTubeApiKey) || string.IsNullOrWhiteSpace(s.YouTubeChannelId))
                throw new InvalidOperationException("YouTube API anahtarı veya Kanal ID boş. Settings'te doldurun.");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            IsRunning = true;

            _runner = Task.Run(() => RunAsync(s, _cts.Token), _cts.Token);
            await Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (!IsRunning) return;
            try { _cts?.Cancel(); } catch { }
            if (_runner is not null)
            {
                try { await _runner.ConfigureAwait(false); } catch { /* swallow */ }
            }
            IsRunning = false;
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        private async Task RunAsync(SettingsData s, CancellationToken ct)
        {
            try
            {
                // 1) Canlı videoId bul
                var videoId = await GetActiveLiveVideoIdAsync(s.YouTubeApiKey, s.YouTubeChannelId, ct);
                if (string.IsNullOrEmpty(videoId))
                    throw new InvalidOperationException("YouTube: Aktif canlı yayın bulunamadı (channelId).");

                // 2) activeLiveChatId al
                var liveChatId = await GetActiveLiveChatIdAsync(s.YouTubeApiKey, videoId, ct);
                if (string.IsNullOrEmpty(liveChatId))
                    throw new InvalidOperationException("YouTube: activeLiveChatId bulunamadı (videos.list).");

                // 3) liveChatMessages.list ile polling
                string? pageToken = null;
                var backoffMs = 1000;

                while (!ct.IsCancellationRequested)
                {
                    var (messages, nextToken, waitMs) =
                        await ListChatMessagesAsync(s.YouTubeApiKey, liveChatId, pageToken, ct);

                    if (messages is not null)
                    {
                        foreach (var m in messages)
                        {
                            OnMessage?.Invoke(m);
                        }
                    }

                    pageToken = nextToken;

                    // API dönen pollingIntervalMillis varsa ona saygı
                    var delay = waitMs > 0 ? waitMs : backoffMs;

                    // Basit backoff: hiçbir mesaj yoksa delay +200ms (üst sınır 5000ms)
                    if (messages is { Count: 0 })
                        backoffMs = Math.Min(backoffMs + 200, 5000);
                    else
                        backoffMs = 1000;

                    await Task.Delay(delay, ct);
                }
            }
            catch (OperationCanceledException) { /* normal stop */ }
            finally
            {
                IsRunning = false;
            }
        }

        // --- Helpers ---

        private async Task<string?> GetActiveLiveVideoIdAsync(string apiKey, string channelId, CancellationToken ct)
        {
            // search.list: canlı videoyu bul
            // https://developers.google.com/youtube/v3/docs/search/list
            var url =
                $"https://www.googleapis.com/youtube/v3/search?part=id&channelId={Uri.EscapeDataString(channelId)}&eventType=live&type=video&key={Uri.EscapeDataString(apiKey)}";
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) &&
                    id.TryGetProperty("videoId", out var vid))
                {
                    return vid.GetString();
                }
            }
            return null;
        }

        private async Task<string?> GetActiveLiveChatIdAsync(string apiKey, string videoId, CancellationToken ct)
        {
            // videos.list: liveStreamingDetails.activeLiveChatId
            // https://developers.google.com/youtube/v3/live/streaming-live-chat
            var url =
                $"https://www.googleapis.com/youtube/v3/videos?part=liveStreamingDetails&id={Uri.EscapeDataString(videoId)}&key={Uri.EscapeDataString(apiKey)}";
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (item.TryGetProperty("liveStreamingDetails", out var lsd) &&
                    lsd.TryGetProperty("activeLiveChatId", out var cid))
                {
                    return cid.GetString();
                }
            }
            return null;
        }

        private async Task<(System.Collections.Generic.List<ChatMessage> messages, string? nextPageToken, int pollingMs)>
            ListChatMessagesAsync(string apiKey, string liveChatId, string? pageToken, CancellationToken ct)
        {
            // liveChatMessages.list: snippet,authorDetails
            // https://developers.google.com/youtube/v3/live/docs/liveChatMessages/list
            var url =
                $"https://www.googleapis.com/youtube/v3/liveChatMessages?part=snippet,authorDetails&liveChatId={Uri.EscapeDataString(liveChatId)}&maxResults=200&key={Uri.EscapeDataString(apiKey)}"
                + (string.IsNullOrEmpty(pageToken) ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");

            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

            var result = new System.Collections.Generic.List<ChatMessage>();
            var root = doc.RootElement;

            var pollMs = root.TryGetProperty("pollingIntervalMillis", out var p) ? p.GetInt32() : 1000;
            var next = root.TryGetProperty("nextPageToken", out var npt) ? npt.GetString() : null;

            if (root.TryGetProperty("items", out var items))
            {
                foreach (var it in items.EnumerateArray())
                {
                    var id = it.TryGetProperty("id", out var idEl) ? idEl.GetString() : Guid.NewGuid().ToString("N");

                    string author = "Unknown";
                    if (it.TryGetProperty("authorDetails", out var ad))
                    {
                        if (ad.TryGetProperty("displayName", out var dn))
                            author = dn.GetString() ?? author;
                    }

                    string text = "";
                    DateTimeOffset ts = DateTimeOffset.UtcNow;

                    if (it.TryGetProperty("snippet", out var sn))
                    {
                        if (sn.TryGetProperty("displayMessage", out var dm))
                            text = dm.GetString() ?? "";
                        if (sn.TryGetProperty("publishedAt", out var pub) &&
                            DateTimeOffset.TryParse(pub.GetString(), out var t))
                            ts = t;
                    }

                    if (string.IsNullOrWhiteSpace(text)) continue;

                    result.Add(new ChatMessage(
                        Id: id ?? Guid.NewGuid().ToString("N"),
                        Source: ChatSource.YouTube,
                        Timestamp: ts,
                        Author: author,
                        Text: text
                    ));
                }
            }

            return (result, next, pollMs);
        }
    }
}
