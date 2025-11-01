using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Core.Chat;
using UniCast.App.Services;           // SettingsStore
using UniCast.Core.Settings;

namespace UniCast.App.Services.Chat
{
    /// <summary>
    /// TikTok canlı chat okuyucu (best-effort, public). Ayar: SettingsData.TikTokRoomId
    /// Not: TikTok resmi bir public API vermediği için WebSocket reverse yöntemi kullanılır.
    /// SettingsData.TikTokRoomId eğer sadece harf/rakam içeriyorsa kullanıcı adı olarak kabul edilir ve room_id resolve edilir.
    /// Numeric ise direkt room_id olarak kullanılır.
    /// </summary>
    public sealed class TikTokChatIngestor : IChatIngestor
    {
        private readonly HttpClient _http;
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _linkedCts;
        private Task? _listenerTask;
        private string? _roomId;

        public event Action<ChatMessage>? OnMessage;
        public string Name => "TikTokChat";
        public bool IsRunning { get; private set; }

        public TikTokChatIngestor(HttpClient? http = null)
        {
            _http = http ?? new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(15);
            if (!_http.DefaultRequestHeaders.UserAgent.ToString().Contains("Mozilla"))
            {
                _http.DefaultRequestHeaders.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            }
        }

        public async Task StartAsync(CancellationToken ct)
        {
            if (IsRunning) return;

            var s = SettingsStore.Load();
            var ident = s.TikTokRoomId?.Trim();

            if (string.IsNullOrWhiteSpace(ident))
                throw new InvalidOperationException("TikTok ayarları eksik: Settings -> TikTokRoomId boş.");

            // room_id mi, kullanıcı adı mı?
            if (IsNumeric(ident))
            {
                _roomId = ident;
            }
            else
            {
                // Kullanıcı adından room_id çöz
                _roomId = await ResolveRoomIdFromUsernameAsync(ident!, ct)
                    ?? throw new InvalidOperationException("TikTok: room_id çözülemedi (kullanıcı yayında mı?).");
            }

            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await ConnectAndListenAsync(_linkedCts.Token);
            IsRunning = true;
        }

        public async Task StopAsync()
        {
            IsRunning = false;
            try { _linkedCts?.Cancel(); } catch { }

            if (_ws is { State: WebSocketState.Open })
            {
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch { /* ignore */ }
            }

            try { await (_listenerTask ?? Task.CompletedTask); } catch { /* ignore */ }

            _ws?.Dispose();
            _ws = null;
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        // ---------- internals ----------

        private async Task ConnectAndListenAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_roomId))
                throw new InvalidOperationException("TikTok: room_id boş.");

            // Bilinen webcast endpointlerinden biri (rev-eng; zamanla değişebilir)
            var wsUrl = new Uri($"wss://webcast5.tiktok.com/ws/room/{_roomId}/?aid=1988&app_language=en&device_platform=web");

            _ws = new ClientWebSocket();
            _ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            await _ws.ConnectAsync(wsUrl, ct);

            _listenerTask = Task.Run(() => ListenLoopAsync(ct), ct);
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            var buffer = new ArraySegment<byte>(new byte[64 * 1024]);
            var sb = new StringBuilder();

            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult? res;

                do
                {
                    res = await _ws.ReceiveAsync(buffer, ct);
                    if (res.MessageType == WebSocketMessageType.Close) return;

                    sb.Append(Encoding.UTF8.GetString(buffer.Array!, 0, res.Count));
                }
                while (!res.EndOfMessage);

                var json = sb.ToString();
                TryParseAndEmit(json);
            }
        }

        private void TryParseAndEmit(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Farklı formatlar görülebiliyor; en yaygın bazı yolları deneriz:
                if (root.TryGetProperty("data", out var data))
                {
                    // 1) data.messages[]
                    if (data.TryGetProperty("messages", out var arr))
                    {
                        foreach (var m in arr.EnumerateArray())
                        {
                            if (!m.TryGetProperty("content", out var c)) continue;

                            var text = c.TryGetProperty("content", out var cc) ? (cc.GetString() ?? "") : "";
                            var author = "User";
                            if (c.TryGetProperty("user", out var u) && u.TryGetProperty("nickname", out var nn))
                                author = nn.GetString() ?? author;

                            if (string.IsNullOrWhiteSpace(text)) continue;

                            OnMessage?.Invoke(new ChatMessage(
                                Id: Guid.NewGuid().ToString("N"),
                                Source: ChatSource.TikTok,
                                Timestamp: DateTimeOffset.Now,
                                Author: author,
                                Text: text
                            ));
                        }
                        return;
                    }
                }

                // 2) Bazı JSON’larda doğrudan "messages" kökte olabilir
                if (root.TryGetProperty("messages", out var messages))
                {
                    foreach (var m in messages.EnumerateArray())
                    {
                        var text = m.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
                        var author = m.TryGetProperty("user", out var u) && u.TryGetProperty("nickname", out var n)
                            ? (n.GetString() ?? "User")
                            : "User";

                        if (string.IsNullOrWhiteSpace(text)) continue;

                        OnMessage?.Invoke(new ChatMessage(
                            Id: Guid.NewGuid().ToString("N"),
                            Source: ChatSource.TikTok,
                            Timestamp: DateTimeOffset.Now,
                            Author: author,
                            Text: text
                        ));
                    }
                }
            }
            catch
            {
                // format değişirse sessiz yut; loglamak istersen buraya yaz
            }
        }

        private static bool IsNumeric(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (!char.IsDigit(s[i])) return false;
            return s.Length > 0;
        }

        private async Task<string?> ResolveRoomIdFromUsernameAsync(string username, CancellationToken ct)
        {
            // Basit HTML scrape ile sayfadaki room_id'yi bulmaya çalışırız.
            // (Yayın kapalıysa ya da TikTok HTML'i değiştiyse null dönebilir.)
            try
            {
                var html = await _http.GetStringAsync($"https://www.tiktok.com/@{username}/live", ct);
                var idx = html.IndexOf("room_id", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return null;

                var colon = html.IndexOf(':', idx);
                if (colon < 0) return null;

                var end = html.IndexOfAny(new[] { ',', '}', '\n', '\r' }, colon + 1);
                if (end < 0) end = html.Length;

                var raw = html.Substring(colon + 1, end - (colon + 1)).Trim().Trim('"');
                return string.IsNullOrWhiteSpace(raw) ? null : raw;
            }
            catch
            {
                return null;
            }
        }
    }
}
