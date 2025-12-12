using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using UniCast.Core.Http;

namespace UniCast.Core.Chat.Ingestors
{
    /// <summary>
    /// Facebook Graph API yardımcı sınıfı.
    /// Live video ID algılama, sayfa bilgisi alma vb.
    /// </summary>
    public static class FacebookApiHelper
    {
        private const string GraphApiBaseUrl = "https://graph.facebook.com/v18.0";

        /// <summary>
        /// Sayfanın aktif live videosunu otomatik algılar.
        /// </summary>
        /// <param name="pageId">Facebook Sayfa ID'si</param>
        /// <param name="pageAccessToken">Sayfa Access Token</param>
        /// <param name="ct">Cancellation Token</param>
        /// <returns>Live video bilgisi veya null</returns>
        public static async Task<FacebookLiveVideo?> GetActiveLiveVideoAsync(
            string pageId,
            string pageAccessToken,
            CancellationToken ct = default)
        {
            try
            {
                // Sayfanın live videolarını al
                var url = $"{GraphApiBaseUrl}/{pageId}/live_videos" +
                          $"?fields=id,title,status,embed_html,permalink_url,live_views" +
                          $"&access_token={pageAccessToken}";

                Log.Debug("[FacebookApi] Live videolar sorgulanıyor: {PageId}", pageId);

                using var response = await SharedHttpClients.GraphApi.GetAsync(url, ct);
                var content = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Error("[FacebookApi] Live video listesi alınamadı: {Content}", content);
                    return null;
                }

                using var doc = JsonDocument.Parse(content);
                var data = doc.RootElement.GetProperty("data");

                foreach (var video in data.EnumerateArray())
                {
                    var status = video.TryGetProperty("status", out var statusProp)
                        ? statusProp.GetString()
                        : null;

                    // Aktif live video mu?
                    if (status == "LIVE" || status == "LIVE_STOPPED")
                    {
                        var liveVideo = new FacebookLiveVideo
                        {
                            Id = video.GetProperty("id").GetString() ?? "",
                            Title = video.TryGetProperty("title", out var titleProp)
                                ? titleProp.GetString()
                                : null,
                            Status = status,
                            LiveViews = video.TryGetProperty("live_views", out var viewsProp)
                                ? viewsProp.GetInt32()
                                : 0,
                            PermalinkUrl = video.TryGetProperty("permalink_url", out var urlProp)
                                ? urlProp.GetString()
                                : null
                        };

                        Log.Information("[FacebookApi] Aktif live video bulundu: {VideoId} - {Title}",
                            liveVideo.Id, liveVideo.Title);

                        return liveVideo;
                    }
                }

                Log.Information("[FacebookApi] Aktif live video bulunamadı");
                return null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FacebookApi] Live video algılama hatası");
                return null;
            }
        }

        /// <summary>
        /// Tüm live videoları listeler (aktif olmayanlar dahil).
        /// </summary>
        public static async Task<List<FacebookLiveVideo>> GetLiveVideosAsync(
            string pageId,
            string pageAccessToken,
            int limit = 10,
            CancellationToken ct = default)
        {
            var videos = new List<FacebookLiveVideo>();

            try
            {
                var url = $"{GraphApiBaseUrl}/{pageId}/live_videos" +
                          $"?fields=id,title,status,created_time,live_views,permalink_url" +
                          $"&limit={limit}" +
                          $"&access_token={pageAccessToken}";

                using var response = await SharedHttpClients.GraphApi.GetAsync(url, ct);
                var content = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Error("[FacebookApi] Live video listesi alınamadı: {Content}", content);
                    return videos;
                }

                using var doc = JsonDocument.Parse(content);
                var data = doc.RootElement.GetProperty("data");

                foreach (var video in data.EnumerateArray())
                {
                    videos.Add(new FacebookLiveVideo
                    {
                        Id = video.GetProperty("id").GetString() ?? "",
                        Title = video.TryGetProperty("title", out var titleProp)
                            ? titleProp.GetString()
                            : null,
                        Status = video.TryGetProperty("status", out var statusProp)
                            ? statusProp.GetString()
                            : null,
                        LiveViews = video.TryGetProperty("live_views", out var viewsProp)
                            ? viewsProp.GetInt32()
                            : 0,
                        PermalinkUrl = video.TryGetProperty("permalink_url", out var urlProp)
                            ? urlProp.GetString()
                            : null,
                        CreatedTime = video.TryGetProperty("created_time", out var timeProp)
                            ? DateTime.Parse(timeProp.GetString() ?? "")
                            : null
                    });
                }

                Log.Debug("[FacebookApi] {Count} live video bulundu", videos.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FacebookApi] Live video listesi alma hatası");
            }

            return videos;
        }

        /// <summary>
        /// Video ID'sinin geçerli olup olmadığını kontrol eder.
        /// </summary>
        public static async Task<bool> ValidateVideoIdAsync(
            string videoId,
            string pageAccessToken,
            CancellationToken ct = default)
        {
            try
            {
                var url = $"{GraphApiBaseUrl}/{videoId}?fields=id,status&access_token={pageAccessToken}";

                using var response = await SharedHttpClients.GraphApi.GetAsync(url, ct);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// URL'den video ID'sini parse eder.
        /// </summary>
        /// <param name="urlOrId">Facebook video URL'si veya direkt ID</param>
        /// <returns>Video ID veya null</returns>
        public static string? ParseVideoId(string urlOrId)
        {
            if (string.IsNullOrWhiteSpace(urlOrId))
                return null;

            urlOrId = urlOrId.Trim();

            // Zaten sadece ID mi?
            if (long.TryParse(urlOrId, out _))
                return urlOrId;

            // URL formatları:
            // https://www.facebook.com/page/videos/123456789/
            // https://www.facebook.com/watch/live/?v=123456789
            // https://fb.watch/xxxxx/
            // https://www.facebook.com/watch?v=123456789

            try
            {
                var uri = new Uri(urlOrId);

                // Query string'den v parametresi
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var vParam = query["v"];
                if (!string.IsNullOrEmpty(vParam) && long.TryParse(vParam, out _))
                    return vParam;

                // Path'ten video ID
                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

                // /page/videos/123456789 formatı
                for (int i = 0; i < segments.Length; i++)
                {
                    if (segments[i] == "videos" && i + 1 < segments.Length)
                    {
                        var videoId = segments[i + 1].TrimEnd('/');
                        if (long.TryParse(videoId, out _))
                            return videoId;
                    }
                }

                // Son segment'i dene
                var lastSegment = segments[^1].TrimEnd('/');
                if (long.TryParse(lastSegment, out _))
                    return lastSegment;
            }
            catch
            {
                // URL parse edilemedi
            }

            return null;
        }

        /// <summary>
        /// Sayfa bilgilerini alır.
        /// </summary>
        public static async Task<FacebookPageInfo?> GetPageInfoAsync(
            string pageId,
            string pageAccessToken,
            CancellationToken ct = default)
        {
            try
            {
                var url = $"{GraphApiBaseUrl}/{pageId}" +
                          $"?fields=id,name,followers_count,fan_count,picture" +
                          $"&access_token={pageAccessToken}";

                using var response = await SharedHttpClients.GraphApi.GetAsync(url, ct);
                var content = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Error("[FacebookApi] Sayfa bilgisi alınamadı: {Content}", content);
                    return null;
                }

                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                return new FacebookPageInfo
                {
                    Id = root.GetProperty("id").GetString() ?? "",
                    Name = root.TryGetProperty("name", out var nameProp)
                        ? nameProp.GetString() ?? ""
                        : "",
                    FollowersCount = root.TryGetProperty("followers_count", out var followersProp)
                        ? followersProp.GetInt32()
                        : 0,
                    FanCount = root.TryGetProperty("fan_count", out var fanProp)
                        ? fanProp.GetInt32()
                        : 0
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FacebookApi] Sayfa bilgisi alma hatası");
                return null;
            }
        }
    }

    /// <summary>
    /// Facebook Live Video bilgisi.
    /// </summary>
    public class FacebookLiveVideo
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; }
        public string? Status { get; set; }
        public int LiveViews { get; set; }
        public string? PermalinkUrl { get; set; }
        public DateTime? CreatedTime { get; set; }

        /// <summary>
        /// Video aktif mi?
        /// </summary>
        public bool IsLive => Status == "LIVE";

        /// <summary>
        /// Video yeni bitti mi?
        /// </summary>
        public bool IsLiveStopped => Status == "LIVE_STOPPED";
    }

    /// <summary>
    /// Facebook Sayfa bilgisi.
    /// </summary>
    public class FacebookPageInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int FollowersCount { get; set; }
        public int FanCount { get; set; }
    }
}