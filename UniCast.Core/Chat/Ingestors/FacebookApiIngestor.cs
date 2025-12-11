using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using UniCast.Core.Http;

namespace UniCast.Core.Chat.Ingestors
{
    /// <summary>
    /// Facebook Graph API kullanarak Live Video yorumlarını çeken ingestor.
    /// 
    /// Gereksinimler:
    /// - Facebook Sayfası (60+ günlük, 100+ takipçi)
    /// - Page Access Token (pages_read_engagement izni)
    /// 
    /// Kullanım:
    /// var ingestor = new FacebookApiIngestor(videoId, pageAccessToken);
    /// await ingestor.StartAsync();
    /// </summary>
    public sealed class FacebookApiIngestor : BaseChatIngestor
    {
        private const string GraphApiBaseUrl = "https://graph.facebook.com/v18.0";
        private const int DefaultPollingIntervalMs = 3000; // 3 saniye
        private const int MinPollingIntervalMs = 1000;
        private const int MaxPollingIntervalMs = 10000;

        private readonly string _videoId;
        private readonly string _pageAccessToken;
        private readonly int _pollingIntervalMs;
        private readonly HashSet<string> _processedCommentIds = new();
        private readonly object _lockObject = new();

        private string? _afterCursor;
        private DateTime _lastPollTime = DateTime.MinValue;
        private int _consecutiveEmptyResponses;
        private int _consecutiveErrors;

        public override ChatPlatform Platform => ChatPlatform.Facebook;

        /// <summary>
        /// Son başarılı polling zamanı
        /// </summary>
        public DateTime LastSuccessfulPoll { get; private set; }

        /// <summary>
        /// Toplam işlenen yorum sayısı
        /// </summary>
        public int TotalCommentsProcessed => _processedCommentIds.Count;

        /// <summary>
        /// FacebookApiIngestor constructor.
        /// </summary>
        /// <param name="videoId">Facebook Live Video ID (sayfa live video'sunun ID'si)</param>
        /// <param name="pageAccessToken">Page Access Token (pages_read_engagement izni gerekli)</param>
        /// <param name="pollingIntervalMs">Polling aralığı (ms), varsayılan 3000ms</param>
        public FacebookApiIngestor(string videoId, string pageAccessToken, int pollingIntervalMs = DefaultPollingIntervalMs)
            : base($"fb-api:{videoId}")
        {
            if (string.IsNullOrWhiteSpace(videoId))
                throw new ArgumentException("Video ID boş olamaz", nameof(videoId));

            if (string.IsNullOrWhiteSpace(pageAccessToken))
                throw new ArgumentException("Page Access Token boş olamaz", nameof(pageAccessToken));

            _videoId = videoId;
            _pageAccessToken = pageAccessToken;
            _pollingIntervalMs = Math.Clamp(pollingIntervalMs, MinPollingIntervalMs, MaxPollingIntervalMs);

            Log.Information("[FacebookApi] Ingestor oluşturuldu - VideoId: {VideoId}, Interval: {Interval}ms",
                _videoId, _pollingIntervalMs);
        }

        protected override Task ConnectAsync(CancellationToken ct)
        {
            // İlk bağlantıda video erişimini test et
            return ValidateVideoAccessAsync(ct);
        }

        protected override Task DisconnectAsync()
        {
            Log.Information("[FacebookApi] Bağlantı kapatıldı - Toplam işlenen: {Count}", _processedCommentIds.Count);

            lock (_lockObject)
            {
                _processedCommentIds.Clear();
                _afterCursor = null;
            }

            return Task.CompletedTask;
        }

        protected override async Task RunMessageLoopAsync(CancellationToken ct)
        {
            Log.Information("[FacebookApi] Mesaj döngüsü başladı - VideoId: {VideoId}", _videoId);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await PollCommentsAsync(ct);
                    _consecutiveErrors = 0;

                    // Adaptive polling: Boş yanıtlar gelirse aralığı artır
                    var actualInterval = _consecutiveEmptyResponses > 5
                        ? Math.Min(_pollingIntervalMs * 2, MaxPollingIntervalMs)
                        : _pollingIntervalMs;

                    await Task.Delay(actualInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    Log.Debug("[FacebookApi] Polling iptal edildi");
                    break;
                }
                catch (HttpRequestException ex)
                {
                    _consecutiveErrors++;
                    Log.Warning(ex, "[FacebookApi] HTTP hatası (deneme {Count})", _consecutiveErrors);

                    if (_consecutiveErrors >= 5)
                    {
                        Log.Error("[FacebookApi] Çok fazla ardışık hata, yeniden bağlanma deneniyor");
                        await ReconnectAsync(ct);
                        _consecutiveErrors = 0;
                    }
                    else
                    {
                        // Exponential backoff
                        var backoffDelay = _pollingIntervalMs * (int)Math.Pow(2, _consecutiveErrors);
                        await Task.Delay(Math.Min(backoffDelay, 30000), ct);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[FacebookApi] Beklenmeyen polling hatası");
                    await Task.Delay(_pollingIntervalMs * 2, ct);
                }
            }

            Log.Information("[FacebookApi] Mesaj döngüsü sonlandı");
        }

        /// <summary>
        /// Video erişimini doğrular.
        /// </summary>
        private async Task ValidateVideoAccessAsync(CancellationToken ct)
        {
            var url = $"{GraphApiBaseUrl}/{_videoId}?fields=id,title,status&access_token={_pageAccessToken}";

            Log.Debug("[FacebookApi] Video erişimi kontrol ediliyor: {VideoId}", _videoId);

            try
            {
                using var response = await SharedHttpClients.GraphApi.GetAsync(url, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(ct);
                    Log.Error("[FacebookApi] Video erişim hatası: {Status} - {Content}",
                        response.StatusCode, errorContent);

                    // Hata detayını parse et
                    var errorInfo = ParseGraphApiError(errorContent);
                    throw new InvalidOperationException(
                        $"Facebook video erişim hatası: {errorInfo.message} (Kod: {errorInfo.code})");
                }

                var content = await response.Content.ReadAsStringAsync(ct);
                Log.Information("[FacebookApi] Video erişimi başarılı: {Response}", content);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException($"Facebook API'ye bağlanılamadı: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Yorumları çeker ve işler.
        /// </summary>
        private async Task PollCommentsAsync(CancellationToken ct)
        {
            var url = BuildCommentsUrl();

            Log.Verbose("[FacebookApi] Polling: {Url}", url.Replace(_pageAccessToken, "***TOKEN***"));

            using var response = await SharedHttpClients.GraphApi.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                var errorInfo = ParseGraphApiError(errorContent);

                // Rate limiting kontrolü
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                    errorInfo.code == 4 || errorInfo.code == 17)
                {
                    Log.Warning("[FacebookApi] Rate limit! Bekleniyor...");
                    await Task.Delay(60000, ct); // 1 dakika bekle
                    return;
                }

                throw new HttpRequestException($"API hatası: {errorInfo.message}");
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var commentsResponse = JsonSerializer.Deserialize<FacebookCommentsResponse>(content);

            if (commentsResponse?.Data == null || commentsResponse.Data.Count == 0)
            {
                _consecutiveEmptyResponses++;
                Log.Verbose("[FacebookApi] Boş yanıt ({Count}. ardışık)", _consecutiveEmptyResponses);
                return;
            }

            _consecutiveEmptyResponses = 0;
            LastSuccessfulPoll = DateTime.UtcNow;

            // Yorumları işle
            var newComments = 0;
            foreach (var comment in commentsResponse.Data)
            {
                if (ProcessComment(comment))
                    newComments++;
            }

            Log.Debug("[FacebookApi] {New}/{Total} yeni yorum işlendi",
                newComments, commentsResponse.Data.Count);

            // Pagination cursor'ı güncelle
            UpdatePagingCursor(commentsResponse.Paging);
        }

        /// <summary>
        /// Comments API URL'i oluşturur.
        /// </summary>
        private string BuildCommentsUrl()
        {
            var fields = "id,from{id,name,picture},message,created_time";
            var url = $"{GraphApiBaseUrl}/{_videoId}/comments?fields={fields}&access_token={_pageAccessToken}&limit=50";

            // Live streaming için filter=stream kullan
            url += "&filter=stream";

            // Cursor varsa ekle
            if (!string.IsNullOrEmpty(_afterCursor))
            {
                url += $"&after={_afterCursor}";
            }

            return url;
        }

        /// <summary>
        /// Tek bir yorumu işler ve ChatMessage olarak yayınlar.
        /// </summary>
        private bool ProcessComment(FacebookComment comment)
        {
            if (comment == null || string.IsNullOrEmpty(comment.Id))
                return false;

            lock (_lockObject)
            {
                // Daha önce işlendiyse atla
                if (!_processedCommentIds.Add(comment.Id))
                    return false;

                // Memory leak önleme
                if (_processedCommentIds.Count > 10000)
                {
                    // En eski yarısını temizle (HashSet sırasız olduğu için tam kontrol yok)
                    _processedCommentIds.Clear();
                    _processedCommentIds.Add(comment.Id);
                    Log.Debug("[FacebookApi] Processed comments cache temizlendi");
                }
            }

            var chatMessage = new ChatMessage
            {
                Id = $"fb_{comment.Id}",
                Platform = ChatPlatform.Facebook,
                Username = comment.From?.Id ?? "unknown",
                DisplayName = comment.From?.Name ?? "Facebook User",
                Message = comment.Message ?? "",
                AvatarUrl = comment.From?.Picture?.Data?.Url,
                Timestamp = ParseFacebookTime(comment.CreatedTime),
                Type = ChatMessageType.Normal,
                Metadata = new Dictionary<string, string>
                {
                    ["comment_id"] = comment.Id,
                    ["video_id"] = _videoId
                }
            };

            Log.Debug("[FacebookApi] Yeni yorum: {User}: {Message}",
                chatMessage.DisplayName, chatMessage.Message);

            PublishMessage(chatMessage);
            return true;
        }

        /// <summary>
        /// Pagination cursor'ı günceller.
        /// </summary>
        private void UpdatePagingCursor(FacebookPaging? paging)
        {
            if (paging?.Cursors?.After != null)
            {
                _afterCursor = paging.Cursors.After;
                Log.Verbose("[FacebookApi] Cursor güncellendi: {Cursor}", _afterCursor?[..Math.Min(20, _afterCursor.Length)]);
            }
        }

        /// <summary>
        /// Facebook zaman formatını parse eder.
        /// </summary>
        private static DateTime ParseFacebookTime(string? timeString)
        {
            if (string.IsNullOrEmpty(timeString))
                return DateTime.UtcNow;

            if (DateTime.TryParse(timeString, out var result))
                return result.ToUniversalTime();

            return DateTime.UtcNow;
        }

        /// <summary>
        /// Graph API hata mesajını parse eder.
        /// </summary>
        private static (string message, int code, int subcode) ParseGraphApiError(string jsonContent)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonContent);
                var error = doc.RootElement.GetProperty("error");

                var message = error.TryGetProperty("message", out var msgProp)
                    ? msgProp.GetString() ?? "Unknown error"
                    : "Unknown error";

                var code = error.TryGetProperty("code", out var codeProp)
                    ? codeProp.GetInt32()
                    : 0;

                var subcode = error.TryGetProperty("error_subcode", out var subProp)
                    ? subProp.GetInt32()
                    : 0;

                return (message, code, subcode);
            }
            catch
            {
                return (jsonContent, 0, 0);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_lockObject)
                {
                    _processedCommentIds.Clear();
                }
            }

            base.Dispose(disposing);
        }

        #region JSON Response Models

        private sealed class FacebookCommentsResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("data")]
            public List<FacebookComment>? Data { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("paging")]
            public FacebookPaging? Paging { get; set; }
        }

        private sealed class FacebookComment
        {
            [System.Text.Json.Serialization.JsonPropertyName("id")]
            public string Id { get; set; } = "";

            [System.Text.Json.Serialization.JsonPropertyName("from")]
            public FacebookUser? From { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("message")]
            public string? Message { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("created_time")]
            public string? CreatedTime { get; set; }
        }

        private sealed class FacebookUser
        {
            [System.Text.Json.Serialization.JsonPropertyName("id")]
            public string? Id { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("name")]
            public string? Name { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("picture")]
            public FacebookPicture? Picture { get; set; }
        }

        private sealed class FacebookPicture
        {
            [System.Text.Json.Serialization.JsonPropertyName("data")]
            public FacebookPictureData? Data { get; set; }
        }

        private sealed class FacebookPictureData
        {
            [System.Text.Json.Serialization.JsonPropertyName("url")]
            public string? Url { get; set; }
        }

        private sealed class FacebookPaging
        {
            [System.Text.Json.Serialization.JsonPropertyName("cursors")]
            public FacebookCursors? Cursors { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("next")]
            public string? Next { get; set; }
        }

        private sealed class FacebookCursors
        {
            [System.Text.Json.Serialization.JsonPropertyName("before")]
            public string? Before { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("after")]
            public string? After { get; set; }
        }

        #endregion
    }
}