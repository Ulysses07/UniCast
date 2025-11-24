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
    /// TikTok WebSocket ingestörü. Polling yapısına adapte edilmiştir.
    /// Bağlantı koparsa Base Class otomatik reconnect yapar.
    /// </summary>
    public sealed class TikTokChatIngestor : PollingChatIngestorBase
    {
        private readonly HttpClient _http;
        private ClientWebSocket? _ws;
        private string _roomId = "";

        // Buffer
        private readonly byte[] _buffer = new byte[64 * 1024];

        public override string Name => "TikTokChat";

        public TikTokChatIngestor(HttpClient? http = null)
        {
            _http = http ?? new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(15);
            if (!_http.DefaultRequestHeaders.UserAgent.ToString().Contains("Mozilla"))
            {
                _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            }
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

            // Oda ID Çözümleme
            if (IsNumeric(ident))
            {
                _roomId = ident;
            }
            else
            {
                _roomId = await ResolveRoomIdFromUsernameAsync(ident, ct)
                    ?? throw new Exception("TikTok: Kullanıcı adından RoomID çözülemedi (Yayın kapalı olabilir).");
            }

            // WebSocket Bağlantısı
            _ws?.Dispose();
            _ws = new ClientWebSocket();
            _ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            var wsUrl = new Uri($"wss://webcast5.tiktok.com/ws/room/{_roomId}/?aid=1988&app_language=en&device_platform=web");
            await _ws.ConnectAsync(wsUrl, ct);
        }

        protected override async Task<(IEnumerable<ChatMessage> messages, int? nextDelayMs)> FetchMessagesAsync(CancellationToken ct)
        {
            // WebSocket kapalıysa hata fırlat (Base class yakalayıp reconnect yapacak)
            if (_ws == null || _ws.State != WebSocketState.Open)
                throw new Exception("WebSocket bağlantısı koptu.");

            var list = new List<ChatMessage>();
            var sb = new StringBuilder();
            WebSocketReceiveResult result;

            // Tek bir mesaj paketini oku
            do
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(_buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new Exception("TikTok sunucusu bağlantıyı kapattı.");

                sb.Append(Encoding.UTF8.GetString(_buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            // Parse Et
            var json = sb.ToString();
            ParseTikTokJson(json, list);

            // TikTok canlı akış olduğu için "bekleme süresi" (NextDelay) düşük olabilir
            // Ancak Base Class mesaj varsa 1sn bekliyor, bu da stabilite için iyidir.
            return (list, 1000);
        }

        public override async Task StopAsync()
        {
            await base.StopAsync();
            _ws?.Dispose();
            _ws = null;
        }

        // --- Helpers ---

        private void ParseTikTokJson(string json, List<ChatMessage> list)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Format 1: data.messages[]
                if (root.TryGetProperty("data", out var data) && data.TryGetProperty("messages", out var arr))
                {
                    foreach (var m in arr.EnumerateArray()) ExtractMessage(m, list);
                }
                // Format 2: messages[] (root)
                else if (root.TryGetProperty("messages", out var messages))
                {
                    foreach (var m in messages.EnumerateArray()) ExtractMessage(m, list);
                }
            }
            catch { /* Parse hatası önemsiz */ }
        }

        private void ExtractMessage(JsonElement m, List<ChatMessage> list)
        {
            string text = "";
            string author = "TikTok User";

            // Farklı JSON yapıları olabiliyor, en yaygın ikisini deniyoruz
            if (m.TryGetProperty("content", out var c)) // İç içe yapı
            {
                text = c.TryGetProperty("content", out var cc) ? (cc.GetString() ?? "") : "";
                if (c.TryGetProperty("user", out var u) && u.TryGetProperty("nickname", out var nn))
                    author = nn.GetString() ?? author;
            }
            else // Düz yapı
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
                var end = html.IndexOfAny(new[] { ',', '}', '"' }, colon + 2); // Basit parse

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