using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.Core.Chat.Ingestors
{
    /// <summary>
    /// Facebook Live Chat Ingestor.
    /// Facebook Graph API Server-Sent Events (SSE) kullanarak gerçek zamanlı yorumları okur.
    /// 
    /// Gereksinimler:
    /// - Facebook Page Access Token (pages_read_engagement, pages_manage_posts izinleri)
    /// - Live Video ID
    /// 
    /// SSE Endpoint: https://streaming-graph.facebook.com/{live-video-id}/live_comments
    /// </summary>
    public sealed class FacebookChatIngestor : BaseChatIngestor
    {
        private const string StreamingGraphUrl = "https://streaming-graph.facebook.com";
        private const string GraphApiUrl = "https://graph.facebook.com/v19.0";

        private readonly HttpClient _httpClient;
        private CancellationTokenSource? _sseCts;

        public override ChatPlatform Platform => ChatPlatform.Facebook;

        /// <summary>
        /// Facebook Page Access Token.
        /// pages_read_engagement ve pages_manage_posts izinleri gerekli.
        /// </summary>
        public string? AccessToken { get; set; }

        /// <summary>
        /// Live Video ID.
        /// Format: {page_id}_{video_id} veya sadece video_id
        /// </summary>
        public string? LiveVideoId { get; set; }

        /// <summary>
        /// Comment rate: "one_per_two_seconds", "ten_per_second", "one_hundred_per_second"
        /// Varsayılan: one_per_two_seconds
        /// </summary>
        public string CommentRate { get; set; } = "one_per_two_seconds";

        /// <summary>
        /// Yeni Facebook chat ingestor oluşturur.
        /// </summary>
        /// <param name="pageIdOrVideoId">Facebook Page ID veya Live Video ID</param>
        public FacebookChatIngestor(string pageIdOrVideoId) : base(pageIdOrVideoId)
        {
            _httpClient = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan // SSE için timeout yok
            };

            _httpClient.DefaultRequestHeaders.Add("Accept", "text/event-stream");
            _httpClient.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
        }

        protected override async Task ConnectAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(AccessToken))
            {
                throw new InvalidOperationException("Facebook Access Token gerekli. " +
                    "Ayarlar > Platform Bağlantıları > Facebook Access Token alanını doldurun.");
            }

            Log.Information("[Facebook] Canlı yayına bağlanılıyor: {Identifier}", _identifier);

            try
            {
                // Live Video ID'yi belirle
                if (string.IsNullOrEmpty(LiveVideoId))
                {
                    LiveVideoId = await FindActiveLiveVideoAsync(ct);

                    if (string.IsNullOrEmpty(LiveVideoId))
                    {
                        throw new Exception($"Aktif canlı yayın bulunamadı. " +
                            "Lütfen önce Facebook'ta canlı yayın başlatın.");
                    }
                }

                Log.Information("[Facebook] Live Video ID: {VideoId}", LiveVideoId);

                // Bağlantıyı test et
                await VerifyAccessAsync(ct);

                Log.Information("[Facebook] Canlı yayına bağlandı");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Facebook] Bağlantı hatası: {Message}", ex.Message);
                throw;
            }
        }

        private async Task<string?> FindActiveLiveVideoAsync(CancellationToken ct)
        {
            try
            {
                // Page'in aktif live video'larını listele
                var url = $"{GraphApiUrl}/{_identifier}/live_videos" +
                         $"?access_token={AccessToken}" +
                         $"&fields=id,status,title,live_views" +
                         $"&broadcast_status=[\"LIVE\"]";

                var response = await _httpClient.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(response);

                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.GetArrayLength() > 0)
                {
                    var firstLive = data[0];
                    if (firstLive.TryGetProperty("id", out var idEl))
                    {
                        var videoId = idEl.GetString();

                        if (firstLive.TryGetProperty("title", out var titleEl))
                        {
                            Log.Information("[Facebook] Aktif yayın bulundu: {Title}",
                                titleEl.GetString());
                        }

                        return videoId;
                    }
                }

                // Alternatif: videos endpoint'inden LIVE olanı bul
                url = $"{GraphApiUrl}/{_identifier}/videos" +
                     $"?access_token={AccessToken}" +
                     $"&fields=id,live_status,title" +
                     $"&limit=10";

                response = await _httpClient.GetStringAsync(url, ct);
                using var doc2 = JsonDocument.Parse(response);

                if (doc2.RootElement.TryGetProperty("data", out var videos))
                {
                    foreach (var video in videos.EnumerateArray())
                    {
                        if (video.TryGetProperty("live_status", out var status) &&
                            status.GetString() == "LIVE" &&
                            video.TryGetProperty("id", out var vid))
                        {
                            return vid.GetString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Facebook] Aktif yayın arama hatası");
            }

            return null;
        }

        private async Task VerifyAccessAsync(CancellationToken ct)
        {
            try
            {
                // Access token'ı doğrula
                var url = $"{GraphApiUrl}/debug_token" +
                         $"?input_token={AccessToken}" +
                         $"&access_token={AccessToken}";

                var response = await _httpClient.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(response);

                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("is_valid", out var isValid) && !isValid.GetBoolean())
                    {
                        throw new Exception("Facebook Access Token geçersiz veya süresi dolmuş");
                    }

                    // İzinleri kontrol et
                    if (data.TryGetProperty("scopes", out var scopes))
                    {
                        var scopeList = new List<string>();
                        foreach (var scope in scopes.EnumerateArray())
                        {
                            scopeList.Add(scope.GetString() ?? "");
                        }

                        Log.Debug("[Facebook] Token izinleri: {Scopes}", string.Join(", ", scopeList));

                        if (!scopeList.Contains("pages_read_engagement"))
                        {
                            Log.Warning("[Facebook] pages_read_engagement izni eksik olabilir");
                        }
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"Facebook API erişim hatası: {ex.Message}");
            }
        }

        protected override async Task DisconnectAsync()
        {
            _sseCts?.Cancel();
            _sseCts?.Dispose();
            _sseCts = null;

            Log.Debug("[Facebook] Bağlantı kapatıldı");
        }

        protected override async Task RunMessageLoopAsync(CancellationToken ct)
        {
            _sseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // SSE bağlantısı kur
            var sseUrl = $"{StreamingGraphUrl}/{LiveVideoId}/live_comments" +
                        $"?access_token={AccessToken}" +
                        $"&comment_rate={CommentRate}" +
                        $"&fields=from{{name,id,picture}},message,created_time";

            Log.Debug("[Facebook] SSE bağlantısı kuruluyor...");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ReadSseStreamAsync(sseUrl, _sseCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("400"))
                {
                    // Live video sona ermiş olabilir
                    Log.Warning("[Facebook] Canlı yayın sona ermiş olabilir: {Message}", ex.Message);
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Facebook] SSE hatası, yeniden bağlanılıyor...");
                    await Task.Delay(5000, ct);
                }
            }
        }

        private async Task ReadSseStreamAsync(string url, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "text/event-stream");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            Log.Information("[Facebook] SSE stream bağlandı");

            var eventData = new StringBuilder();

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();

                if (line == null)
                {
                    // Stream kapandı
                    throw new Exception("SSE stream kapandı");
                }

                if (line.StartsWith("data:"))
                {
                    var data = line.Substring(5).Trim();
                    eventData.Append(data);
                }
                else if (string.IsNullOrEmpty(line) && eventData.Length > 0)
                {
                    // Event tamamlandı
                    try
                    {
                        var json = eventData.ToString();
                        var message = ParseComment(json);

                        if (message != null)
                        {
                            PublishMessage(message);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "[Facebook] Yorum parse hatası");
                    }

                    eventData.Clear();
                }
            }
        }

        private ChatMessage? ParseComment(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string? userId = null;
                string? userName = null;
                string? message = null;
                string? avatarUrl = null;
                DateTime timestamp = DateTime.UtcNow;

                // From bilgisi
                if (root.TryGetProperty("from", out var from))
                {
                    userId = from.TryGetProperty("id", out var id) ? id.GetString() : null;
                    userName = from.TryGetProperty("name", out var name) ? name.GetString() : null;

                    // Avatar URL
                    if (from.TryGetProperty("picture", out var picture) &&
                        picture.TryGetProperty("data", out var picData) &&
                        picData.TryGetProperty("url", out var picUrl))
                    {
                        avatarUrl = picUrl.GetString();
                    }
                }

                // Mesaj
                if (root.TryGetProperty("message", out var msg))
                {
                    message = msg.GetString();
                }

                // Timestamp
                if (root.TryGetProperty("created_time", out var createdTime))
                {
                    if (DateTime.TryParse(createdTime.GetString(), out var parsed))
                    {
                        timestamp = parsed;
                    }
                }

                if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(message))
                {
                    return null;
                }

                var chatMessage = new ChatMessage
                {
                    Platform = ChatPlatform.Facebook,
                    Username = userId ?? userName,
                    DisplayName = userName,
                    Message = message,
                    AvatarUrl = avatarUrl,
                    Timestamp = timestamp
                };

                // Comment ID metadata
                if (root.TryGetProperty("id", out var commentId))
                {
                    chatMessage.Metadata["comment_id"] = commentId.GetString() ?? "";
                }

                return chatMessage;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[Facebook] JSON parse hatası: {Json}",
                    json.Length > 200 ? json.Substring(0, 200) + "..." : json);
                return null;
            }
        }

        /// <summary>
        /// Canlı yayına yorum gönderir.
        /// </summary>
        /// <param name="message">Gönderilecek mesaj</param>
        public async Task SendCommentAsync(string message, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(AccessToken) || string.IsNullOrEmpty(LiveVideoId))
            {
                Log.Warning("[Facebook] Yorum göndermek için bağlı olmalısınız");
                return;
            }

            try
            {
                var url = $"{GraphApiUrl}/{LiveVideoId}/comments";
                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["message"] = message,
                    ["access_token"] = AccessToken
                });

                var response = await _httpClient.PostAsync(url, content, ct);
                response.EnsureSuccessStatusCode();

                Log.Debug("[Facebook] Yorum gönderildi");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Facebook] Yorum gönderme hatası");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _sseCts?.Dispose();
                _httpClient.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}