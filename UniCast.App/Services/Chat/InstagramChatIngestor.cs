using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Core.Chat;
using UniCast.App.Services;
using UniCast.Core.Settings;

namespace UniCast.App.Services.Chat
{
    public sealed class InstagramChatIngestor : PollingChatIngestorBase
    {
        private readonly HttpClient _http;

        private string _sessionId = "";
        private string _userId = "";
        private string _broadcastId = "";
        private string _cursor = "";

        public override string Name => "InstagramChat";

        public InstagramChatIngestor(HttpClient? http = null)
        {
            _http = http ?? new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        protected override void ValidateSettings()
        {
            var s = SettingsStore.Load();
            _sessionId = (s.InstagramSessionId ?? "").Trim();
            _userId = (s.InstagramUserId ?? "").Trim();

            if (string.IsNullOrWhiteSpace(_sessionId) || string.IsNullOrWhiteSpace(_userId))
                throw new InvalidOperationException("Instagram SessionID veya UserID eksik.");

            InitHeaders();
        }

        protected override async Task InitializeAsync(CancellationToken ct)
        {
            // DÜZELTME BURADA:
            // ResolveLiveBroadcastId null dönerse boş string'e çeviriyoruz (?? string.Empty).
            // Böylece 'Olası null ataması' uyarısı kalkıyor.
            _broadcastId = await ResolveLiveBroadcastId(ct) ?? string.Empty;

            if (string.IsNullOrEmpty(_broadcastId))
                throw new Exception("Instagram: Kullanıcı şu an canlı yayında değil (veya ID bulunamadı).");

            _cursor = "";
        }

        protected override async Task<(IEnumerable<ChatMessage> messages, int? nextDelayMs)> FetchMessagesAsync(CancellationToken ct)
        {
            var list = new List<ChatMessage>();

            var url = $"https://i.instagram.com/api/v1/live/{_broadcastId}/get_comment/";
            if (!string.IsNullOrWhiteSpace(_cursor)) url += $"?max_id={_cursor}";

            var resp = await _http.GetStringAsync(url, ct);
            using var json = JsonDocument.Parse(resp);

            if (json.RootElement.TryGetProperty("comments", out var comments))
            {
                foreach (var c in comments.EnumerateArray())
                {
                    var text = c.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var user = "Instagram User";
                    if (c.TryGetProperty("user", out var u) && u.TryGetProperty("username", out var un))
                        user = un.GetString() ?? user;

                    list.Add(new ChatMessage(
                        Id: Guid.NewGuid().ToString("N"),
                        Source: ChatSource.Instagram,
                        Timestamp: DateTimeOffset.Now,
                        Author: user,
                        Text: text
                    ));
                }

                if (json.RootElement.TryGetProperty("next_max_id", out var nxt))
                    _cursor = nxt.GetString() ?? "";
            }

            return (list, 2000);
        }

        private void InitHeaders()
        {
            _http.DefaultRequestHeaders.Clear();
            _http.DefaultRequestHeaders.Add("User-Agent", "Instagram 287.0.0.27.109 Android");
            _http.DefaultRequestHeaders.Add("Cookie", $"sessionid={_sessionId}");
            _http.DefaultRequestHeaders.Add("X-IG-App-ID", "567067343352427");
        }

        private async Task<string?> ResolveLiveBroadcastId(CancellationToken ct)
        {
            try
            {
                var url = $"https://i.instagram.com/api/v1/live/get_user_live_status/?user_ids={_userId}";
                var resp = await _http.GetStringAsync(url, ct);
                using var json = JsonDocument.Parse(resp);

                if (!json.RootElement.TryGetProperty("statuses", out var arr) || arr.GetArrayLength() == 0)
                    return null;

                var st = arr[0];
                if (!st.TryGetProperty("has_live_stream", out var hasLiveEl) || !hasLiveEl.GetBoolean())
                    return null;

                if (st.TryGetProperty("live_video_id", out var vid))
                    return vid.GetString();
            }
            catch
            {
                // Hata durumunda null dön
            }
            return null;
        }
    }
}