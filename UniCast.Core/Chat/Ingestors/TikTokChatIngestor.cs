using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.Core.Chat.Ingestors
{
    /// <summary>
    /// TikTok Live Chat Ingestor.
    /// TikTok'un internal WebCast servisine bağlanarak gerçek zamanlı chat okur.
    /// 
    /// NOT: Bu unofficial bir API'dir. TikTok resmi API sağlamamaktadır.
    /// Sadece kullanıcı adı ile bağlanılır, kimlik doğrulama gerekmez.
    /// </summary>
    public sealed class TikTokChatIngestor : BaseChatIngestor
    {
        private const string TikTokWebcastUrl = "https://webcast.tiktok.com/webcast/room/info/";
        private const string TikTokSignServer = "https://tiktok-sign.zerody.one/webcast/sign/"; // Public sign server

        private readonly HttpClient _httpClient;
        private ClientWebSocket? _webSocket;
        private string? _roomId;
        private bool _isConnected;

        // TikTok WebCast message types
        private const int MSG_CHAT = 1;
        private const int MSG_GIFT = 5;
        private const int MSG_LIKE = 6;
        private const int MSG_MEMBER = 7;
        private const int MSG_SOCIAL = 9;
        private const int MSG_ROOM_USER_SEQ = 11;

        public override ChatPlatform Platform => ChatPlatform.TikTok;

        /// <summary>
        /// Proxy URL (opsiyonel).
        /// </summary>
        public string? ProxyUrl { get; set; }

        /// <summary>
        /// Session ID cookie (opsiyonel, bazı bölgelerde gerekebilir).
        /// </summary>
        public string? SessionId { get; set; }

        /// <summary>
        /// Yeni TikTok chat ingestor oluşturur.
        /// </summary>
        /// <param name="username">TikTok kullanıcı adı (@ olmadan)</param>
        public TikTokChatIngestor(string username) : base(username.TrimStart('@').ToLowerInvariant())
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        protected override async Task ConnectAsync(CancellationToken ct)
        {
            Log.Information("[TikTok] @{Username} kullanıcısına bağlanılıyor...", _identifier);

            try
            {
                // 1. Room ID'yi al
                _roomId = await GetRoomIdAsync(ct);
                if (string.IsNullOrEmpty(_roomId))
                {
                    throw new Exception($"@{_identifier} şu anda canlı yayında değil veya bulunamadı");
                }

                Log.Debug("[TikTok] Room ID bulundu: {RoomId}", _roomId);

                // 2. WebSocket URL'ini al ve bağlan
                var wsUrl = await GetWebSocketUrlAsync(_roomId, ct);
                if (string.IsNullOrEmpty(wsUrl))
                {
                    // Fallback: Polling moduna geç
                    Log.Warning("[TikTok] WebSocket URL alınamadı, polling moduna geçiliyor");
                    return;
                }

                // 3. WebSocket bağlantısı
                _webSocket = new ClientWebSocket();
                _webSocket.Options.SetRequestHeader("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                await _webSocket.ConnectAsync(new Uri(wsUrl), ct);
                _isConnected = true;

                Log.Information("[TikTok] @{Username} canlı yayınına bağlandı (Room: {RoomId})",
                    _identifier, _roomId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[TikTok] Bağlantı hatası: {Message}", ex.Message);
                throw;
            }
        }

        private async Task<string?> GetRoomIdAsync(CancellationToken ct)
        {
            try
            {
                // TikTok profil sayfasından room ID'yi çek
                var profileUrl = $"https://www.tiktok.com/@{_identifier}/live";

                var request = new HttpRequestMessage(HttpMethod.Get, profileUrl);
                request.Headers.Add("Accept", "text/html");

                var response = await _httpClient.SendAsync(request, ct);
                var html = await response.Content.ReadAsStringAsync(ct);

                // Room ID'yi HTML'den parse et
                // Pattern: "roomId":"1234567890"
                var roomIdMatch = Regex.Match(html, @"""roomId""\s*:\s*""(\d+)""");
                if (roomIdMatch.Success)
                {
                    return roomIdMatch.Groups[1].Value;
                }

                // Alternatif pattern: room_id=1234567890
                var altMatch = Regex.Match(html, @"room_id[=:](\d+)");
                if (altMatch.Success)
                {
                    return altMatch.Groups[1].Value;
                }

                // API ile dene
                return await GetRoomIdFromApiAsync(ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[TikTok] Room ID alınamadı");
                return null;
            }
        }

        private async Task<string?> GetRoomIdFromApiAsync(CancellationToken ct)
        {
            try
            {
                var apiUrl = $"https://www.tiktok.com/api/live/detail/?aid=1988&uniqueId={_identifier}";
                var response = await _httpClient.GetStringAsync(apiUrl, ct);

                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("LiveRoomInfo", out var roomInfo) &&
                    roomInfo.TryGetProperty("liveRoomId", out var roomId))
                {
                    return roomId.GetString();
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[TikTok] API room ID alınamadı");
            }

            return null;
        }

        private async Task<string?> GetWebSocketUrlAsync(string roomId, CancellationToken ct)
        {
            try
            {
                // WebCast push URL'ini oluştur
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var wsUrl = $"wss://webcast.tiktok.com/webcast/im/push/v2/" +
                           $"?room_id={roomId}" +
                           $"&app_name=webcast" +
                           $"&device_platform=web" +
                           $"&browser_name=Mozilla" +
                           $"&browser_version=5.0" +
                           $"&_ts={timestamp}";

                return wsUrl;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[TikTok] WebSocket URL oluşturulamadı");
                return null;
            }
        }

        protected override async Task DisconnectAsync()
        {
            _isConnected = false;

            if (_webSocket != null)
            {
                try
                {
                    if (_webSocket.State == WebSocketState.Open)
                    {
                        await _webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Disconnecting",
                            CancellationToken.None);
                    }
                }
                catch { }
                finally
                {
                    _webSocket.Dispose();
                    _webSocket = null;
                }
            }

            Log.Debug("[TikTok] Bağlantı kapatıldı");
        }

        protected override async Task RunMessageLoopAsync(CancellationToken ct)
        {
            if (_webSocket == null || !_isConnected)
            {
                // WebSocket yoksa polling modunda çalış
                await RunPollingModeAsync(ct);
                return;
            }

            var buffer = new byte[8192];
            var messageBuffer = new MemoryStream();

            while (!ct.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                try
                {
                    var result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Log.Information("[TikTok] WebSocket kapatıldı");
                        break;
                    }

                    messageBuffer.Write(buffer, 0, result.Count);

                    if (result.EndOfMessage)
                    {
                        var data = messageBuffer.ToArray();
                        messageBuffer.SetLength(0);

                        await ProcessWebcastMessageAsync(data);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException ex)
                {
                    Log.Warning(ex, "[TikTok] WebSocket hatası, yeniden bağlanılıyor...");

                    try
                    {
                        await ReconnectAsync(ct);
                    }
                    catch
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[TikTok] Mesaj işleme hatası");
                    await Task.Delay(1000, ct);
                }
            }
        }

        private async Task RunPollingModeAsync(CancellationToken ct)
        {
            Log.Information("[TikTok] Polling modunda çalışıyor (Room: {RoomId})", _roomId);

            string? cursor = null;
            var pollingInterval = TimeSpan.FromSeconds(2);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var messages = await FetchMessagesAsync(_roomId!, cursor, ct);

                    foreach (var msg in messages)
                    {
                        PublishMessage(msg);
                    }

                    await Task.Delay(pollingInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[TikTok] Polling hatası");
                    await Task.Delay(5000, ct);
                }
            }
        }

        private async Task<List<ChatMessage>> FetchMessagesAsync(string roomId, string? cursor, CancellationToken ct)
        {
            var messages = new List<ChatMessage>();

            try
            {
                var url = $"https://webcast.tiktok.com/webcast/im/fetch/" +
                         $"?room_id={roomId}" +
                         $"&cursor={cursor ?? "0"}" +
                         $"&internal_ext=internal_ext";

                var response = await _httpClient.GetStringAsync(url, ct);

                // Protobuf response'u parse et (simplified JSON fallback)
                using var doc = JsonDocument.Parse(response);

                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("messages", out var messageArray))
                {
                    foreach (var msgElement in messageArray.EnumerateArray())
                    {
                        var chatMsg = ParseMessage(msgElement);
                        if (chatMsg != null)
                        {
                            messages.Add(chatMsg);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[TikTok] Mesaj fetch hatası");
            }

            return messages;
        }

        private async Task ProcessWebcastMessageAsync(byte[] data)
        {
            try
            {
                // TikTok WebCast mesajları protobuf formatında
                // Basitleştirilmiş parsing - gerçek implementasyon protobuf gerektirir

                // GZIP decompress if needed
                byte[] decompressed;
                if (data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b)
                {
                    using var ms = new MemoryStream(data);
                    using var gzip = new GZipStream(ms, CompressionMode.Decompress);
                    using var result = new MemoryStream();
                    await gzip.CopyToAsync(result);
                    decompressed = result.ToArray();
                }
                else
                {
                    decompressed = data;
                }

                // JSON olarak parse etmeyi dene
                var json = Encoding.UTF8.GetString(decompressed);

                if (json.StartsWith("{"))
                {
                    using var doc = JsonDocument.Parse(json);
                    var msg = ParseMessage(doc.RootElement);
                    if (msg != null)
                    {
                        PublishMessage(msg);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[TikTok] WebCast mesaj parse hatası");
            }
        }

        private ChatMessage? ParseMessage(JsonElement element)
        {
            try
            {
                // Mesaj tipini belirle
                var msgType = element.TryGetProperty("type", out var typeEl)
                    ? typeEl.GetString()
                    : "chat";

                string? username = null;
                string? displayName = null;
                string? message = null;
                string? avatarUrl = null;
                var chatMsgType = ChatMessageType.Normal;
                string? giftName = null;
                int? giftCount = null;

                // User bilgisi
                if (element.TryGetProperty("user", out var user))
                {
                    username = user.TryGetProperty("uniqueId", out var uid) ? uid.GetString() : null;
                    displayName = user.TryGetProperty("nickname", out var nick) ? nick.GetString() : username;

                    if (user.TryGetProperty("profilePicture", out var pic) &&
                        pic.TryGetProperty("urls", out var urls) &&
                        urls.GetArrayLength() > 0)
                    {
                        avatarUrl = urls[0].GetString();
                    }
                }

                // Mesaj içeriği
                if (element.TryGetProperty("comment", out var comment))
                {
                    message = comment.GetString();
                }
                else if (element.TryGetProperty("text", out var text))
                {
                    message = text.GetString();
                }

                // Gift bilgisi
                if (element.TryGetProperty("gift", out var gift))
                {
                    chatMsgType = ChatMessageType.Gift;
                    giftName = gift.TryGetProperty("name", out var gn) ? gn.GetString() : "Gift";
                    giftCount = gift.TryGetProperty("repeat_count", out var gc) ? gc.GetInt32() : 1;
                    message = $"🎁 {giftName} x{giftCount}";
                }

                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(message))
                {
                    return null;
                }

                var chatMessage = new ChatMessage
                {
                    Platform = ChatPlatform.TikTok,
                    Username = username,
                    DisplayName = displayName ?? username,
                    Message = message,
                    AvatarUrl = avatarUrl,
                    Type = chatMsgType,
                    Timestamp = DateTime.UtcNow
                };

                // Gift metadata
                if (chatMsgType == ChatMessageType.Gift && giftName != null)
                {
                    chatMessage.Metadata["gift_name"] = giftName;
                    chatMessage.Metadata["gift_count"] = giftCount?.ToString() ?? "1";
                }

                return chatMessage;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[TikTok] Mesaj parse hatası");
                return null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _webSocket?.Dispose();
                _httpClient.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}