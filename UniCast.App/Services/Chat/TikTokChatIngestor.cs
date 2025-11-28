using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Core.Chat;
using UniCast.App.Services;
using UniCast.Core.Settings;

namespace UniCast.App.Services.Chat
{
    /// <summary>
    /// TikTok WebSocket ingestörü.
    /// DÜZELTME: HttpClient factory + WebSocket reconnect mekanizması
    /// </summary>
    public sealed class TikTokChatIngestor : PollingChatIngestorBase
    {
        private readonly HttpClient _http;
        private ClientWebSocket? _ws;
        private string _roomId = "";

        private readonly byte[] _buffer = new byte[64 * 1024];

        // DÜZELTME: Reconnect sayacı
        private int _reconnectAttempts = 0;
        private const int MAX_RECONNECT_ATTEMPTS = 5;

        public override string Name => "TikTokChat";

        public TikTokChatIngestor(HttpClient? http = null)
        {
            // DÜZELTME: Factory pattern
            _http = http ?? HttpClientFactory.TikTok;
        }

        protected override void ValidateSettings()
        {
            var s = SettingsStore.Load();
            if (string.IsNullOrWhiteSpace(s.TikTokRoomId))
                throw new InvalidOperationException("TikTok RoomID (veya Kullanıcı Adı) eksik.");
        }

        protected override async Task InitializeAsync(CancellationToken ct)
        {
            var s = SettingsStore.Load();
            var ident = (s.TikTokRoomId ?? "").Trim();

            if (IsNumeric(ident))
            {
                _roomId = ident;
            }
            else
            {
                _roomId = await ResolveRoomIdFromUsernameAsync(ident, ct)
                    ?? throw new Exception("TikTok: Kullanıcı adından RoomID çözülemedi (Yayın kapalı olabilir).");
            }

            await ConnectWebSocketAsync(ct);
        }

        // DÜZELTME: WebSocket bağlantısını ayrı metoda aldık (reconnect için)
        private async Task ConnectWebSocketAsync(CancellationToken ct)
        {
            // Eski bağlantıyı temizle
            if (_ws != null)
            {
                try
                {
                    if (_ws.State == WebSocketState.Open)
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", ct);
                }
                catch { }
                finally
                {
                    _ws.Dispose();
                    _ws = null;
                }
            }

            _ws = new ClientWebSocket();
            _ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            var wsUrl = new Uri($"wss://webcast5.tiktok.com/ws/room/{_roomId}/?aid=1988&app_language=en&device_platform=web");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            await _ws.ConnectAsync(wsUrl, timeoutCts.Token);
            _reconnectAttempts = 0; // Başarılı bağlantıda sıfırla
        }

        protected override async Task<(IEnumerable<ChatMessage> messages, int? nextDelayMs)> FetchMessagesAsync(CancellationToken ct)
        {
            // DÜZELTME: WebSocket kapalıysa reconnect dene
            if (_ws == null || _ws.State != WebSocketState.Open)
            {
                if (_reconnectAttempts >= MAX_RECONNECT_ATTEMPTS)
                    throw new Exception($"TikTok: Maksimum yeniden bağlanma denemesi aşıldı ({MAX_RECONNECT_ATTEMPTS})");

                _reconnectAttempts++;
                var delay = Math.Min(1000 * _reconnectAttempts, 5000);

                System.Diagnostics.Debug.WriteLine($"[TikTok] WebSocket reconnect deneme {_reconnectAttempts}/{MAX_RECONNECT_ATTEMPTS}");

                await Task.Delay(delay, ct);
                await ConnectWebSocketAsync(ct);
            }

            var list = new List<ChatMessage>();
            var sb = new StringBuilder();
            WebSocketReceiveResult result;

            // Timeout ile okuma
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(TimeSpan.FromSeconds(10));

            try
            {
                do
                {
                    result = await _ws!.ReceiveAsync(new ArraySegment<byte>(_buffer), readCts.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        System.Diagnostics.Debug.WriteLine("[TikTok] Sunucu bağlantıyı kapattı");
                        throw new Exception("TikTok sunucusu bağlantıyı kapattı.");
                    }

                    sb.Append(Encoding.UTF8.GetString(_buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                var json = sb.ToString();
                ParseTikTokJson(json, list);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Read timeout - boş liste dön, bir sonraki iterasyonda tekrar dene
                System.Diagnostics.Debug.WriteLine("[TikTok] Okuma timeout");
            }

            return (list, 1000);
        }

        public override async Task StopAsync()
        {
            await base.StopAsync();

            if (_ws != null)
            {
                try
                {
                    if (_ws.State == WebSocketState.Open)
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopping", cts.Token);
                    }
                }
                catch { }
                finally
                {
                    _ws.Dispose();
                    _ws = null;
                }
            }
        }

        private void ParseTikTokJson(string json, List<ChatMessage> list)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("data", out var data) && data.TryGetProperty("messages", out var arr))
                {
                    foreach (var m in arr.EnumerateArray()) ExtractMessage(m, list);
                }
                else if (root.TryGetProperty("messages", out var messages))
                {
                    foreach (var m in messages.EnumerateArray()) ExtractMessage(m, list);
                }
            }
            catch { }
        }

        private void ExtractMessage(JsonElement m, List<ChatMessage> list)
        {
            string text = "";
            string author = "TikTok User";

            if (m.TryGetProperty("content", out var c))
            {
                text = c.TryGetProperty("content", out var cc) ? (cc.GetString() ?? "") : "";
                if (c.TryGetProperty("user", out var u) && u.TryGetProperty("nickname", out var nn))
                    author = nn.GetString() ?? author;
            }
            else
            {
                text = m.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
                if (m.TryGetProperty("user", out var u2) && u2.TryGetProperty("nickname", out var nn2))
                    author = nn2.GetString() ?? author;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                list.Add(new ChatMessage(Guid.NewGuid().ToString("N"), ChatSource.TikTok, DateTimeOffset.Now, author, text));
            }
        }

        private static bool IsNumeric(string s)
        {
            foreach (char c in s) if (!char.IsDigit(c)) return false;
            return s.Length > 0;
        }

        private async Task<string?> ResolveRoomIdFromUsernameAsync(string username, CancellationToken ct)
        {
            try
            {
                var html = await _http.GetStringAsync($"https://www.tiktok.com/@{username}/live", ct);
                var idx = html.IndexOf("room_id", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return null;

                var colon = html.IndexOf(':', idx);
                var end = html.IndexOfAny(new[] { ',', '}', '"' }, colon + 2);

                if (colon > 0 && end > colon)
                {
                    var id = html.Substring(colon + 1, end - (colon + 1)).Trim().Replace("\"", "");
                    return IsNumeric(id) ? id : null;
                }
            }
            catch { }
            return null;
        }
    }
}