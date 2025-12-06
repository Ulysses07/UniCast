using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.Core.Chat.Ingestors
{
    /// <summary>
    /// Facebook Live Chat Scraper.
    /// WebView2'den alınan cookie'ler ile Facebook Live Chat'i scrape eder.
    /// API key veya token gerektirmez - sadece kullanıcı login'i yeterli.
    /// </summary>
    public sealed class FacebookChatScraper : BaseChatIngestor
    {
        private const string FacebookGraphQL = "https://www.facebook.com/api/graphql/";
        private const string FacebookMBasic = "https://mbasic.facebook.com";

        private readonly HttpClient _httpClient;
        private readonly HashSet<string> _seenMessageIds = new();

        private string? _cookies;
        private string? _fbDtsg;
        private string? _userId;
        private string? _liveVideoId;
        private int _pollingIntervalMs = 3000;

        public override ChatPlatform Platform => ChatPlatform.Facebook;

        /// <summary>
        /// Facebook cookie'leri (WebView2'den alınır).
        /// </summary>
        public string? Cookies
        {
            get => _cookies;
            set => _cookies = value;
        }

        /// <summary>
        /// Facebook User ID (c_user cookie'sinden).
        /// </summary>
        public string? UserId
        {
            get => _userId;
            set => _userId = value;
        }

        /// <summary>
        /// Live Video ID veya URL.
        /// </summary>
        public string? LiveVideoId
        {
            get => _liveVideoId;
            set => _liveVideoId = ExtractVideoId(value);
        }

        /// <summary>
        /// Yeni Facebook chat scraper oluşturur.
        /// </summary>
        /// <param name="liveVideoIdOrUrl">Live Video ID veya URL</param>
        public FacebookChatScraper(string liveVideoIdOrUrl) : base(liveVideoIdOrUrl)
        {
            _liveVideoId = ExtractVideoId(liveVideoIdOrUrl);

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false // Manuel cookie yönetimi
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            // Browser gibi görünmek için header'lar
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "tr-TR,tr;q=0.9,en-US;q=0.8,en;q=0.7");
            _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        }

        /// <summary>
        /// Video ID'yi URL'den çıkarır.
        /// </summary>
        private string? ExtractVideoId(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            input = input.Trim();

            // Zaten sadece ID ise
            if (Regex.IsMatch(input, @"^\d+$"))
                return input;

            // URL formatları:
            // https://www.facebook.com/pagename/videos/123456789/
            // https://www.facebook.com/watch/live/?v=123456789
            // https://www.facebook.com/watch/?v=123456789
            // https://fb.watch/xxxxx/

            // /videos/ID/ formatı
            var match = Regex.Match(input, @"/videos/(\d+)");
            if (match.Success)
                return match.Groups[1].Value;

            // ?v=ID formatı
            match = Regex.Match(input, @"[?&]v=(\d+)");
            if (match.Success)
                return match.Groups[1].Value;

            // fb.watch kısa link - bu durumda expand etmemiz gerekir
            if (input.Contains("fb.watch"))
            {
                Log.Warning("[Facebook Scraper] fb.watch linkleri desteklenmiyor, tam URL kullanın");
            }

            return input;
        }

        protected override async Task ConnectAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_cookies))
            {
                throw new InvalidOperationException(
                    "Facebook cookie'leri gerekli. Önce Facebook hesabınıza giriş yapın.");
            }

            if (string.IsNullOrEmpty(_liveVideoId))
            {
                throw new InvalidOperationException(
                    "Live Video ID gerekli. Facebook canlı yayın linkini girin.");
            }

            Log.Information("[Facebook Scraper] Bağlanılıyor. Video ID: {VideoId}", _liveVideoId);

            // fb_dtsg token'ını al
            await ExtractFbDtsgAsync(ct);

            // Bağlantıyı test et
            await VerifyConnectionAsync(ct);

            Log.Information("[Facebook Scraper] Bağlantı başarılı");
        }

        /// <summary>
        /// Facebook'un CSRF token'ını (fb_dtsg) çıkarır.
        /// </summary>
        private async Task ExtractFbDtsgAsync(CancellationToken ct)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "https://www.facebook.com/");
                request.Headers.Add("Cookie", _cookies);

                var response = await _httpClient.SendAsync(request, ct);
                var html = await response.Content.ReadAsStringAsync(ct);

                // fb_dtsg token'ını bul
                // "DTSGInitialData":[],{"token":"..."}
                var dtsgMatch = Regex.Match(html, @"""DTSGInitialData""[^}]*""token"":""([^""]+)""");
                if (dtsgMatch.Success)
                {
                    _fbDtsg = dtsgMatch.Groups[1].Value;
                    Log.Debug("[Facebook Scraper] fb_dtsg token alındı");
                    return;
                }

                // Alternatif format
                dtsgMatch = Regex.Match(html, @"name=""fb_dtsg""\s+value=""([^""]+)""");
                if (dtsgMatch.Success)
                {
                    _fbDtsg = dtsgMatch.Groups[1].Value;
                    Log.Debug("[Facebook Scraper] fb_dtsg token alındı (alternatif)");
                    return;
                }

                // Başka bir format
                dtsgMatch = Regex.Match(html, @"""dtsg"":\{""token"":""([^""]+)""");
                if (dtsgMatch.Success)
                {
                    _fbDtsg = dtsgMatch.Groups[1].Value;
                    Log.Debug("[Facebook Scraper] fb_dtsg token alındı (json)");
                    return;
                }

                Log.Warning("[Facebook Scraper] fb_dtsg token bulunamadı, GraphQL çalışmayabilir");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Facebook Scraper] fb_dtsg çıkarma hatası");
            }
        }

        /// <summary>
        /// Bağlantıyı doğrular.
        /// </summary>
        private async Task VerifyConnectionAsync(CancellationToken ct)
        {
            try
            {
                // Video sayfasına erişimi test et
                var videoUrl = $"https://www.facebook.com/watch/live/?v={_liveVideoId}";
                var request = new HttpRequestMessage(HttpMethod.Get, videoUrl);
                request.Headers.Add("Cookie", _cookies);

                var response = await _httpClient.SendAsync(request, ct);

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Video sayfasına erişilemedi: {response.StatusCode}");
                }

                var html = await response.Content.ReadAsStringAsync(ct);

                // Login kontrolü
                if (html.Contains("login") && html.Contains("password") && !html.Contains("logout"))
                {
                    throw new Exception("Facebook oturumu geçersiz. Lütfen tekrar giriş yapın.");
                }

                Log.Debug("[Facebook Scraper] Video sayfasına erişim doğrulandı");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Facebook Scraper] Bağlantı doğrulama hatası");
                throw;
            }
        }

        protected override Task DisconnectAsync()
        {
            _seenMessageIds.Clear();
            Log.Debug("[Facebook Scraper] Bağlantı kapatıldı");
            return Task.CompletedTask;
        }

        protected override async Task RunMessageLoopAsync(CancellationToken ct)
        {
            Log.Information("[Facebook Scraper] Mesaj döngüsü başladı");

            while (!ct.IsCancellationRequested && State == ConnectionState.Connected)
            {
                try
                {
                    await FetchCommentsAsync(ct);
                    await Task.Delay(_pollingIntervalMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Facebook Scraper] Yorum çekme hatası");
                    await Task.Delay(5000, ct);
                }
            }
        }

        /// <summary>
        /// Yorumları çeker - mbasic.facebook.com kullanır (daha basit HTML).
        /// </summary>
        private async Task FetchCommentsAsync(CancellationToken ct)
        {
            try
            {
                // mbasic.facebook.com daha basit HTML döndürür, parse etmesi kolay
                var url = $"{FacebookMBasic}/video.php?v={_liveVideoId}";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Cookie", _cookies);

                var response = await _httpClient.SendAsync(request, ct);
                var html = await response.Content.ReadAsStringAsync(ct);

                // Yorumları parse et
                ParseMBasicComments(html);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[Facebook Scraper] mbasic fetch hatası, alternatif deneniyor");

                // Alternatif: Normal Facebook
                await FetchCommentsAlternativeAsync(ct);
            }
        }

        /// <summary>
        /// mbasic.facebook.com HTML'inden yorumları parse eder.
        /// </summary>
        private void ParseMBasicComments(string html)
        {
            try
            {
                // mbasic formatı daha basit, regex ile parse edilebilir
                // <div class="..comment..">
                //   <a href="/profile.php?id=123">UserName</a>
                //   <div>Comment text</div>
                // </div>

                // Yorum bloklarını bul
                var commentPattern = @"<div[^>]*(?:comment|_2b1j)[^>]*>.*?<a[^>]*href=""([^""]*?)""[^>]*>([^<]+)</a>.*?<div[^>]*>([^<]+)</div>";
                var matches = Regex.Matches(html, commentPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);

                foreach (Match match in matches)
                {
                    try
                    {
                        var profileUrl = match.Groups[1].Value;
                        var userName = WebUtility.HtmlDecode(match.Groups[2].Value.Trim());
                        var messageText = WebUtility.HtmlDecode(match.Groups[3].Value.Trim());

                        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(messageText))
                            continue;

                        // User ID'yi URL'den çıkar
                        var userId = ExtractUserIdFromUrl(profileUrl) ?? userName;

                        // Benzersiz ID oluştur
                        var messageId = $"{userId}_{messageText.GetHashCode()}";

                        if (_seenMessageIds.Contains(messageId))
                            continue;

                        _seenMessageIds.Add(messageId);

                        // Eski mesajları temizle (memory leak önleme)
                        if (_seenMessageIds.Count > 1000)
                        {
                            _seenMessageIds.Clear();
                        }

                        var chatMessage = new ChatMessage
                        {
                            Platform = ChatPlatform.Facebook,
                            Username = userId,
                            DisplayName = userName,
                            Message = messageText,
                            Timestamp = DateTime.UtcNow
                        };

                        PublishMessage(chatMessage);
                        Log.Debug("[Facebook Scraper] Yeni mesaj: {User}: {Message}", userName, messageText);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "[Facebook Scraper] Yorum parse hatası");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Facebook Scraper] HTML parse hatası");
            }
        }

        /// <summary>
        /// Alternatif yorum çekme metodu - GraphQL.
        /// </summary>
        private async Task FetchCommentsAlternativeAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_fbDtsg))
            {
                Log.Debug("[Facebook Scraper] fb_dtsg yok, GraphQL atlanıyor");
                return;
            }

            try
            {
                // GraphQL sorgusu
                var variables = JsonSerializer.Serialize(new
                {
                    videoID = _liveVideoId,
                    first = 50
                });

                var formData = new Dictionary<string, string>
                {
                    ["fb_dtsg"] = _fbDtsg,
                    ["variables"] = variables,
                    ["doc_id"] = "5765609026858656" // LiveVideoComments query ID
                };

                var request = new HttpRequestMessage(HttpMethod.Post, FacebookGraphQL)
                {
                    Content = new FormUrlEncodedContent(formData)
                };
                request.Headers.Add("Cookie", _cookies);
                request.Headers.Add("X-FB-Friendly-Name", "LiveVideoCommentListQuery");

                var response = await _httpClient.SendAsync(request, ct);
                var json = await response.Content.ReadAsStringAsync(ct);

                ParseGraphQLComments(json);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[Facebook Scraper] GraphQL fetch hatası");
            }
        }

        /// <summary>
        /// GraphQL yanıtından yorumları parse eder.
        /// </summary>
        private void ParseGraphQLComments(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // data.video.comment_rendering_instance.comments.edges
                if (!root.TryGetProperty("data", out var data))
                    return;

                // JSON yapısı karmaşık olabilir, farklı yolları dene
                JsonElement? comments = null;

                if (data.TryGetProperty("video", out var video) &&
                    video.TryGetProperty("comment_rendering_instance", out var cri) &&
                    cri.TryGetProperty("comments", out var commentsEl) &&
                    commentsEl.TryGetProperty("edges", out var edges))
                {
                    comments = edges;
                }
                else if (data.TryGetProperty("node", out var node) &&
                         node.TryGetProperty("feedback", out var feedback) &&
                         feedback.TryGetProperty("comment_rendering_instance", out var cri2) &&
                         cri2.TryGetProperty("comments", out var commentsEl2) &&
                         commentsEl2.TryGetProperty("edges", out var edges2))
                {
                    comments = edges2;
                }

                if (comments == null)
                    return;

                foreach (var edge in comments.Value.EnumerateArray())
                {
                    try
                    {
                        if (!edge.TryGetProperty("node", out var nodeEl))
                            continue;

                        var commentId = nodeEl.TryGetProperty("id", out var idEl)
                            ? idEl.GetString() : null;

                        if (string.IsNullOrEmpty(commentId) || _seenMessageIds.Contains(commentId))
                            continue;

                        _seenMessageIds.Add(commentId);

                        string? userId = null;
                        string? userName = null;
                        string? avatarUrl = null;
                        string? messageText = null;

                        // Kullanıcı bilgisi
                        if (nodeEl.TryGetProperty("author", out var author))
                        {
                            userId = author.TryGetProperty("id", out var uid) ? uid.GetString() : null;
                            userName = author.TryGetProperty("name", out var name) ? name.GetString() : null;

                            if (author.TryGetProperty("profile_picture", out var pic) &&
                                pic.TryGetProperty("uri", out var uri))
                            {
                                avatarUrl = uri.GetString();
                            }
                        }

                        // Mesaj
                        if (nodeEl.TryGetProperty("body", out var body) &&
                            body.TryGetProperty("text", out var text))
                        {
                            messageText = text.GetString();
                        }

                        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(messageText))
                            continue;

                        var chatMessage = new ChatMessage
                        {
                            Platform = ChatPlatform.Facebook,
                            Username = userId ?? userName,
                            DisplayName = userName,
                            Message = messageText,
                            AvatarUrl = avatarUrl,
                            Timestamp = DateTime.UtcNow
                        };

                        PublishMessage(chatMessage);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "[Facebook Scraper] Comment node parse hatası");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[Facebook Scraper] GraphQL JSON parse hatası");
            }
        }

        /// <summary>
        /// Profil URL'sinden user ID'yi çıkarır.
        /// </summary>
        private string? ExtractUserIdFromUrl(string url)
        {
            // /profile.php?id=123456789
            var match = Regex.Match(url, @"id=(\d+)");
            if (match.Success)
                return match.Groups[1].Value;

            // /username
            match = Regex.Match(url, @"/([^/?]+)(?:\?|$)");
            if (match.Success)
                return match.Groups[1].Value;

            return null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _httpClient.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}