using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniCast.App.Services;
using UniCast.Core.Chat;
using UniCast.Core.Settings;

namespace UniCast.App.Services.Chat
{
    public sealed class FacebookChatIngestor : PollingChatIngestorBase
    {
        private readonly HttpClient _http;
        private const string GraphBase = "https://graph.facebook.com/v19.0";

        // State
        private string _accessToken = "";
        private string _liveVideoId = "";
        private string _cursorAfter = "";

        public override string Name => "FacebookChat";

        public FacebookChatIngestor(HttpClient? http = null)
        {
            _http = http ?? new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(15);
            if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
                _http.DefaultRequestHeaders.Add("User-Agent", "UniCast-FacebookChat/1.0");
        }

        protected override void ValidateSettings()
        {
            var s = SettingsStore.Load();
            _accessToken = (s.FacebookAccessToken ?? "").Trim();

            // PageId veya LiveVideoId'den en az biri olmalı
            if (string.IsNullOrWhiteSpace(_accessToken))
                throw new InvalidOperationException("Facebook Access Token eksik.");

            if (string.IsNullOrWhiteSpace(s.FacebookPageId) && string.IsNullOrWhiteSpace(s.FacebookLiveVideoId))
                throw new InvalidOperationException("Facebook PageID veya LiveVideoID gerekli.");
        }

        protected override async Task InitializeAsync(CancellationToken ct)
        {
            var s = SettingsStore.Load();
            var directId = (s.FacebookLiveVideoId ?? "").Trim();
            var pageId = (s.FacebookPageId ?? "").Trim();

            if (!string.IsNullOrWhiteSpace(directId))
            {
                _liveVideoId = directId;
            }
            else
            {
                _liveVideoId = await ResolveLiveVideoIdFromPageAsync(pageId, ct)
                               ?? throw new Exception("Facebook: Sayfada aktif canlı yayın bulunamadı.");
            }

            _cursorAfter = ""; // Cursor sıfırla
        }

        protected override async Task<(IEnumerable<ChatMessage> messages, int? nextDelayMs)> FetchMessagesAsync(CancellationToken ct)
        {
            var list = new List<ChatMessage>();

            var url = $"{GraphBase}/{_liveVideoId}/comments?order=reverse_chronological&filter=stream&fields=from{{name}},message,created_time&access_token={Uri.EscapeDataString(_accessToken)}";

            if (!string.IsNullOrWhiteSpace(_cursorAfter))
                url += $"&after={Uri.EscapeDataString(_cursorAfter)}";

            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var arr))
            {
                foreach (var c in arr.EnumerateArray())
                {
                    var msg = c.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(msg)) continue;

                    var author = "Facebook User";
                    if (c.TryGetProperty("from", out var f) && f.TryGetProperty("name", out var n))
                        author = n.GetString() ?? author;

                    var ts = DateTimeOffset.Now;
                    if (c.TryGetProperty("created_time", out var ctProp))
                        DateTimeOffset.TryParse(ctProp.GetString(), out ts);

                    list.Add(new ChatMessage(
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
                _cursorAfter = after.GetString() ?? "";
            }

            return (list, 1500); // 1.5 sn bekleme
        }

        private async Task<string?> ResolveLiveVideoIdFromPageAsync(string pageId, CancellationToken ct)
        {
            var url = $"{GraphBase}/{pageId}/live_videos?status=LIVE_NOW&fields=id&access_token={Uri.EscapeDataString(_accessToken)}";
            var json = await _http.GetStringAsync(url, ct);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                return null;

            return data[0].GetProperty("id").GetString();
        }
    }
}