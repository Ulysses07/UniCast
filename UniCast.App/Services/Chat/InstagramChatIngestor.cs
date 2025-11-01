using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Core.Chat;
using UniCast.App.Services;
using UniCast.Core.Settings;

namespace UniCast.App.Services.Chat
{
    public sealed class InstagramChatIngestor : IChatIngestor
    {
        public string Name => "InstagramChat";
        public bool IsRunning { get; private set; }
        public event Action<ChatMessage>? OnMessage;

        private readonly HttpClient _http;
        private CancellationTokenSource? _linked;
        private string? _sessionId;
        private string? _userId;
        private string? _broadcastId;

        public InstagramChatIngestor(HttpClient? http = null)
        {
            _http = http ?? new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        public async Task StartAsync(CancellationToken ct)
        {
            if (IsRunning) return;

            var s = SettingsStore.Load();
            _sessionId = (s.InstagramSessionId ?? "").Trim();
            _userId = (s.InstagramUserId ?? "").Trim();

            if (string.IsNullOrWhiteSpace(_sessionId) || string.IsNullOrWhiteSpace(_userId))
                throw new InvalidOperationException("Instagram SessionID veya UserID eksik (Ayarlar).");

            InitHeaders();

            _broadcastId = await ResolveLiveBroadcastId(ct);
            if (string.IsNullOrEmpty(_broadcastId))
                throw new InvalidOperationException("Instagram: Kullanıcı şu an canlı yayında değil (broadcast bulunamadı).");

            _linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(() => PollChat(_linked.Token), _linked.Token);

            IsRunning = true;
        }

        public Task StopAsync()
        {
            IsRunning = false;
            try { _linked?.Cancel(); } catch { }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => new ValueTask(StopAsync());

        private void InitHeaders()
        {
            _http.DefaultRequestHeaders.Clear();
            // Android IG UA (yeterli):
            _http.DefaultRequestHeaders.Add("User-Agent", "Instagram 287.0.0.27.109 Android");
            _http.DefaultRequestHeaders.Add("Cookie", $"sessionid={_sessionId}");
            _http.DefaultRequestHeaders.Add("X-IG-App-ID", "567067343352427"); // public app id (mobil)
        }

        private async Task<string?> ResolveLiveBroadcastId(CancellationToken ct)
        {
            // Kullanıcının canlıda olup olmadığını kontrol eder; live_video_id döner
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

            return null;
        }

        private async Task PollChat(CancellationToken ct)
        {
            string cursor = "";

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var url = $"https://i.instagram.com/api/v1/live/{_broadcastId}/get_comment/";
                    if (!string.IsNullOrWhiteSpace(cursor)) url += $"?max_id={cursor}";

                    var resp = await _http.GetStringAsync(url, ct);
                    using var json = JsonDocument.Parse(resp);

                    if (json.RootElement.TryGetProperty("comments", out var comments))
                    {
                        foreach (var c in comments.EnumerateArray())
                        {
                            var text = c.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
                            if (string.IsNullOrWhiteSpace(text)) continue;

                            var user = "user";
                            if (c.TryGetProperty("user", out var u) &&
                                u.TryGetProperty("username", out var un))
                                user = un.GetString() ?? user;

                            OnMessage?.Invoke(new ChatMessage(
                                Id: Guid.NewGuid().ToString("N"),
                                Source: ChatSource.Instagram,
                                Timestamp: DateTimeOffset.Now,
                                Author: user,
                                Text: text
                            ));
                        }

                        if (json.RootElement.TryGetProperty("next_max_id", out var nxt))
                            cursor = nxt.GetString() ?? "";
                    }

                    await Task.Delay(1200, ct); // IG rate-limit güvenli bekleme
                }
                catch (OperationCanceledException) { /* normal stop */ }
                catch
                {
                    // geçici hata/backoff
                    await Task.Delay(2000, ct);
                }
            }
        }
    }
}
