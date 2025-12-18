using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Serilog;

namespace UniCast.Core.Chat.Bridge
{
    /// <summary>
    /// Browser Extension'dan gelen chat mesajlarını alan WebSocket server.
    /// Extension, kullanıcının tarayıcısında Instagram Live sayfasındaki
    /// yorumları okuyup bu server'a gönderir.
    /// </summary>
    public sealed class ExtensionBridgeServer : IDisposable
    {
        private const int DEFAULT_PORT = 9876;
        private readonly int _port;
        private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
        private CancellationTokenSource? _cts;
        private HttpListener? _listener;
        private Task? _acceptTask;
        private bool _disposed;

        /// <summary>
        /// Yeni bir chat mesajı geldiğinde tetiklenir
        /// </summary>
        public event Action<ChatMessage>? OnMessageReceived;

        /// <summary>
        /// Extension bağlandığında tetiklenir
        /// </summary>
        public event Action<string>? OnClientConnected;

        /// <summary>
        /// Extension bağlantısı koptuğunda tetiklenir
        /// </summary>
        public event Action<string>? OnClientDisconnected;

        /// <summary>
        /// Bağlı client sayısı
        /// </summary>
        public int ClientCount => _clients.Count;

        /// <summary>
        /// Server çalışıyor mu
        /// </summary>
        public bool IsRunning { get; private set; }

        public ExtensionBridgeServer(int port = DEFAULT_PORT)
        {
            _port = port;
        }

        /// <summary>
        /// WebSocket server'ı başlat
        /// </summary>
        public async Task StartAsync()
        {
            if (IsRunning) return;

            try
            {
                // Her start için yeni CTS oluştur
                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Start();
                IsRunning = true;

                Log.Information("[ExtensionBridge] WebSocket server başlatıldı: ws://localhost:{Port}", _port);

                _acceptTask = AcceptClientsAsync(_cts.Token);
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5) // Access denied
            {
                Log.Error("[ExtensionBridge] Port {Port} için yetki yok. Admin olarak çalıştırın veya farklı port deneyin.", _port);
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ExtensionBridge] Server başlatma hatası");
                throw;
            }
        }

        /// <summary>
        /// Server'ı durdur
        /// </summary>
        public async Task StopAsync()
        {
            if (!IsRunning) return;

            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // CTS zaten dispose edilmiş, devam et
            }

            IsRunning = false;

            // Tüm client bağlantılarını kapat
            foreach (var kvp in _clients)
            {
                try
                {
                    if (kvp.Value.State == WebSocketState.Open)
                    {
                        await kvp.Value.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Server kapatılıyor",
                            CancellationToken.None);
                    }
                }
                catch { }
            }
            _clients.Clear();

            _listener?.Stop();
            _listener?.Close();

            if (_acceptTask != null)
            {
                try
                {
                    await _acceptTask;
                }
                catch (OperationCanceledException) { }
            }

            Log.Information("[ExtensionBridge] Server durduruldu");
        }

        private async Task AcceptClientsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var context = await _listener.GetContextAsync();

                    if (context.Request.IsWebSocketRequest)
                    {
                        _ = HandleWebSocketAsync(context, ct);
                    }
                    else
                    {
                        // Normal HTTP request - ping/status için
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/json";
                        var response = JsonSerializer.Serialize(new { status = "ok", clients = ClientCount });
                        var buffer = Encoding.UTF8.GetBytes(response);
                        await context.Response.OutputStream.WriteAsync(buffer, ct);
                        context.Response.Close();
                    }
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[ExtensionBridge] Client accept hatası");
                }
            }
        }

        private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken ct)
        {
            WebSocket? ws = null;
            var clientId = Guid.NewGuid().ToString("N")[..8];

            try
            {
                var wsContext = await context.AcceptWebSocketAsync(null);
                ws = wsContext.WebSocket;

                _clients.TryAdd(clientId, ws);
                Log.Information("[ExtensionBridge] Client bağlandı: {ClientId}, Path: {Path}",
                    clientId, context.Request.Url?.AbsolutePath);

                OnClientConnected?.Invoke(clientId);

                var buffer = new byte[4096];

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(buffer, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        await ProcessMessageAsync(json, clientId);
                    }
                }
            }
            catch (WebSocketException ex)
            {
                Log.Debug("[ExtensionBridge] WebSocket hatası (Client: {ClientId}): {Message}", clientId, ex.Message);
            }
            catch (OperationCanceledException)
            {
                // Normal kapatma
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[ExtensionBridge] Client işleme hatası: {ClientId}", clientId);
            }
            finally
            {
                _clients.TryRemove(clientId, out _);

                if (ws != null)
                {
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        }
                        ws.Dispose();
                    }
                    catch { }
                }

                Log.Information("[ExtensionBridge] Client ayrıldı: {ClientId}", clientId);
                OnClientDisconnected?.Invoke(clientId);
            }
        }

        private async Task ProcessMessageAsync(string json, string clientId)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl))
                {
                    return;
                }

                var type = typeEl.GetString();
                Log.Debug("[ExtensionBridge] Mesaj alındı: Type={Type}, Client={ClientId}", type, clientId);

                switch (type)
                {
                    case "comment":
                        if (root.TryGetProperty("data", out var dataEl))
                        {
                            var message = ParseCommentData(dataEl);
                            if (message != null)
                            {
                                Log.Debug("[ExtensionBridge] Yorum [{Platform}]: @{User}: {Text}",
                                    message.Platform, message.DisplayName, message.Message);
                                OnMessageReceived?.Invoke(message);
                            }
                        }
                        break;

                    case "connected":
                        if (root.TryGetProperty("url", out var urlEl))
                        {
                            Log.Information("[ExtensionBridge] Extension bağlandı: {Url}", urlEl.GetString());
                        }
                        break;

                    case "pong":
                        // Ping response - ignore
                        break;

                    case "status":
                        Log.Debug("[ExtensionBridge] Extension durumu: {Json}", json);
                        break;
                }
            }
            catch (JsonException ex)
            {
                Log.Warning("[ExtensionBridge] JSON parse hatası: {Message}", ex.Message);
            }
        }

        private ChatMessage? ParseCommentData(JsonElement data)
        {
            try
            {
                var username = data.TryGetProperty("username", out var userEl)
                    ? userEl.GetString() ?? "Anonim"
                    : "Anonim";

                var text = data.TryGetProperty("text", out var textEl)
                    ? textEl.GetString() ?? ""
                    : "";

                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                var id = data.TryGetProperty("id", out var idEl)
                    ? idEl.GetString() ?? Guid.NewGuid().ToString()
                    : Guid.NewGuid().ToString();

                var timestamp = data.TryGetProperty("timestamp", out var tsEl)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(tsEl.GetInt64()).DateTime
                    : DateTime.Now;

                // Platform belirleme - varsayılan Instagram
                var platform = ChatPlatform.Instagram;
                var platformStr = "instagram"; // varsayılan
                
                if (data.TryGetProperty("platform", out var platformEl))
                {
                    platformStr = platformEl.GetString()?.ToLowerInvariant() ?? "instagram";
                    platform = platformStr switch
                    {
                        "tiktok" => ChatPlatform.TikTok,
                        "instagram" => ChatPlatform.Instagram,
                        "facebook" => ChatPlatform.Facebook,
                        "youtube" => ChatPlatform.YouTube,
                        _ => ChatPlatform.Instagram
                    };
                    Log.Debug("[ExtensionBridge] Platform algılandı: {PlatformStr} -> {Platform}", platformStr, platform);
                }
                else
                {
                    Log.Warning("[ExtensionBridge] Platform bilgisi YOK! Varsayılan Instagram kullanılıyor. Data: {Data}", data.ToString());
                }

                return new ChatMessage
                {
                    Id = id,
                    Platform = platform,
                    Username = username.ToLowerInvariant(),
                    DisplayName = username,
                    Message = text,
                    Timestamp = timestamp,
                    IsModerator = false,
                    IsOwner = false,
                    IsVerified = false
                };
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[ExtensionBridge] Comment parse hatası");
                return null;
            }
        }

        /// <summary>
        /// Tüm client'lara mesaj gönder
        /// </summary>
        public async Task BroadcastAsync(object message)
        {
            var json = JsonSerializer.Serialize(message);
            var buffer = Encoding.UTF8.GetBytes(json);
            var segment = new ArraySegment<byte>(buffer);

            foreach (var kvp in _clients)
            {
                try
                {
                    if (kvp.Value.State == WebSocketState.Open)
                    {
                        await kvp.Value.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[ExtensionBridge] Broadcast hatası: {ClientId}", kvp.Key);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException) { }

            _cts?.Dispose();
            _listener?.Close();

            foreach (var kvp in _clients)
            {
                try { kvp.Value.Dispose(); } catch { }
            }
            _clients.Clear();
        }
    }
}