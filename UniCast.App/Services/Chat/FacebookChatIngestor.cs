using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniCast.App.Services;          // SettingsStore
using UniCast.Core.Chat;
using UniCast.Core.Settings;

namespace UniCast.App.Services.Chat
{
    /// <summary>
    /// Facebook Live chat ingestörü (Graph API).
    /// Gerekli ayarlar:
    ///   - Settings.FacebookAccessToken : Page Access Token (pages_read_engagement izni dahil)
    ///   - (Tercihen) Settings.FacebookLiveVideoId  : canlı video id
    ///   - veya Settings.FacebookPageId            : sayfadaki aktif canlı yayından id çözülür
    /// </summary>
    public sealed class FacebookChatIngestor : IChatIngestor
    {
        public string Name => "FacebookChat";
        public bool IsRunning { get; private set; }
        public event Action<ChatMessage>? OnMessage;

        private readonly HttpClient _http;
        private string? _accessToken;
        private string? _liveVideoId;
        private string _graphBase = "https://graph.facebook.com/v19.0";
        private CancellationTokenSource? _linked;
        private string? _cursorAfter;

        public FacebookChatIngestor(HttpClient? http = null)
        {
            _http = http ?? new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(15);
            _http.DefaultRequestHeaders.Add("User-Agent", "UniCast-FacebookChat/1.0");
        }

        public async Task StartAsync(CancellationToken ct)
        {
            if (IsRunning) return;

            var s = SettingsStore.Load();
            _accessToken = (s.FacebookAccessToken ?? "").Trim();
            var pageId = (s.FacebookPageId ?? "").Trim();
            var directId = (s.FacebookLiveVideoId ?? "").Trim();

            if (string.IsNullOrWhiteSpace(_accessToken))
                throw new InvalidOperationException("Facebook: Access Token boş (Ayarlar).");

            // Canlı video id belirle
            if (!string.IsNullOrWhiteSpace(directId))
            {
                _liveVideoId = directId;
            }
            else if (!string.IsNullOrWhiteSpace(pageId))
            {
                _liveVideoId = await ResolveLiveVideoIdFromPageAsync(pageId, ct);
                if (string.IsNullOrWhiteSpace(_liveVideoId))
                    throw new InvalidOperationException("Facebook: Canlı video bulunamadı (sayfada canlı yok).");
            }
            else
            {
                throw new InvalidOperationException("Facebook: PageId veya LiveVideoId gerekli.");
            }

            _linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(() => PollCommentsAsync(_linked.Token), _linked.Token);

            IsRunning = true;
        }

        public Task StopAsync()
        {
            IsRunning = false;
            try { _linked?.Cancel(); } catch { }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => new ValueTask(StopAsync());

        // ------------------------------------------------------

        private async Task<string?> ResolveLiveVideoIdFromPageAsync(string pageId, CancellationToken ct)
        {
            // Aktif canlı yayın id’sini bul:
            // GET /{pageId}/live_videos?status=LIVE_NOW
            var url = $"{_graphBase}/{pageId}/live_videos?status=LIVE_NOW&fields=id&access_token={Uri.EscapeDataString(_accessToken!)}";
            var json = await _http.GetStringAsync(url, ct);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                return null;

            var id = data[0].GetProperty("id").GetString();
            return id;
        }

        private async Task PollCommentsAsync(CancellationToken ct)
        {
            // Akış: /{liveVideoId}/comments
            //  - order=reverse_chronological (yeniler önce)
            //  - filter=stream (canlı akış)
            //  - fields=from{name},message,created_time
            //  - page başına cursor "after" ile ilerlenir
            var baseUrl =
                $"{_graphBase}/{_liveVideoId}/comments?order=reverse_chronological&filter=stream&fields=from{{name}},message,created_time&access_token={Uri.EscapeDataString(_accessToken!)}";

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var url = baseUrl;
                    if (!string.IsNullOrWhiteSpace(_cursorAfter))
                        url += $"&after={Uri.EscapeDataString(_cursorAfter)}";

                    var json = await _http.GetStringAsync(url, ct);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("data", out var arr))
                    {
                        // Facebook reverse_chronological dediğimiz için genelde yeni mesajlar üstte geliyor.
                        foreach (var c in arr.EnumerateArray())
                        {
                            var msg = c.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
                            if (string.IsNullOrWhiteSpace(msg)) continue;

                            var author = "User";
                            if (c.TryGetProperty("from", out var f) && f.TryGetProperty("name", out var n))
                                author = n.GetString() ?? author;

                            var ts = DateTimeOffset.Now;
                            if (c.TryGetProperty("created_time", out var ctProp))
                                DateTimeOffset.TryParse(ctProp.GetString(), out ts);

                            OnMessage?.Invoke(new ChatMessage(
                                Id: Guid.NewGuid().ToString("N"),
                                Source: ChatSource.Facebook,
                                Timestamp: ts,
                                Author: author,
                                Text: msg
                            ));
                        }
                    }

                    if (root.TryGetProperty("paging", out var paging)
                        && paging.TryGetProperty("cursors", out var cursors)
                        && cursors.TryGetProperty("after", out var after))
                    {
                        _cursorAfter = after.GetString();
                    }

                    await Task.Delay(1200, ct); // rate-limit güvenli
                }
                catch (OperationCanceledException) { /* normal stop */ }
                catch
                {
                    // Geçici hatalarda kısa bekleyip devam
                    await Task.Delay(2000, ct);
                }
            }
        }
    }
}
