using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InstagramApiSharp;
using InstagramApiSharp.API;
using InstagramApiSharp.API.Builder;
using InstagramApiSharp.Classes;
using InstagramApiSharp.Classes.Models;
using InstagramApiSharp.Logger;
using Serilog;

namespace UniCast.Core.Chat.Ingestors
{
    /// <summary>
    /// Hibrit Instagram Live Chat Ingestor.
    /// Private API ve Graph API'yi AYNI ANDA kullanarak:
    /// - Her API 4 saniyede bir çağrılır (güvenli)
    /// - Kullanıcı 2 saniyede bir yeni yorum görür (hızlı)
    /// - Bir API fail ederse diğeri devam eder (redundant)
    /// </summary>
    public sealed class InstagramHybridChatIngestor : BaseChatIngestor
    {
        #region Constants

        private const string GraphApiUrl = "https://graph.facebook.com/v19.0";

        #endregion

        #region Private API Fields

        private IInstaApi? _instaApi;
        private string? _broadcastId;
        private string _lastCommentTs = "0";
        private bool _isPrivateApiLoggedIn = false;
        private readonly string _sessionFile;

        // Private API durumu
        private bool _privateApiEnabled = true;
        private int _privateApiFailCount = 0;
        private DateTime _privateApiDisabledUntil = DateTime.MinValue;

        #endregion

        #region Graph API Fields

        private readonly HttpClient _httpClient;
        private string? _liveMediaId;
        private string? _lastGraphCommentCursor;

        // Graph API durumu
        private bool _graphApiEnabled = true;
        private int _graphApiRequestsThisHour = 0;
        private DateTime _graphApiHourStart = DateTime.UtcNow;

        #endregion

        #region Shared Fields

        // Duplicate önleme
        private readonly ConcurrentDictionary<string, byte> _processedComments = new();
        private DateTime _lastCleanup = DateTime.UtcNow;

        #endregion

        #region Properties

        public override ChatPlatform Platform => ChatPlatform.Instagram;

        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string? GraphApiAccessToken { get; set; }
        public string? BroadcasterUsername { get; set; }
        public TimeSpan TotalPollingInterval { get; set; } = TimeSpan.FromSeconds(4);
        public string? TwoFactorCode { get; set; }

        #endregion

        #region Constructor

        public InstagramHybridChatIngestor(string username) : base(username.TrimStart('@').ToLowerInvariant())
        {
            Username = _identifier;

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var unicastFolder = Path.Combine(appData, "UniCast", "Sessions");
            Directory.CreateDirectory(unicastFolder);
            _sessionFile = Path.Combine(unicastFolder, $"instagram_hybrid_{Username}.session");

            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        #endregion

        #region Connection

        protected override async Task ConnectAsync(CancellationToken ct)
        {
            Log.Information("[Instagram Hybrid] @{Username} hesabına bağlanılıyor...", Username);

            var tasks = new List<Task>();

            if (!string.IsNullOrEmpty(Password))
            {
                tasks.Add(InitializePrivateApiAsync(ct));
            }
            else
            {
                _privateApiEnabled = false;
            }

            if (!string.IsNullOrEmpty(GraphApiAccessToken))
            {
                tasks.Add(InitializeGraphApiAsync(ct));
            }
            else
            {
                _graphApiEnabled = false;
            }

            if (tasks.Count == 0)
            {
                throw new InvalidOperationException("Instagram için şifre veya Access Token gerekli.");
            }

            await Task.WhenAll(tasks);

            if (!_privateApiEnabled && !_graphApiEnabled)
            {
                throw new Exception("Instagram'a bağlanılamadı.");
            }

            Log.Information("[Instagram Hybrid] Bağlantı kuruldu - Private: {P}, Graph: {G}",
                _privateApiEnabled ? "✓" : "✗", _graphApiEnabled ? "✓" : "✗");
        }

        private async Task InitializePrivateApiAsync(CancellationToken ct)
        {
            try
            {
                _instaApi = InstaApiBuilder.CreateBuilder()
                    .SetUser(new UserSessionData { UserName = Username, Password = Password })
                    .UseLogger(new InstaApiSerilogLogger())
                    .SetRequestDelay(RequestDelay.FromSeconds(1, 2))
                    .Build();

                // Session yükle
                if (File.Exists(_sessionFile))
                {
                    try
                    {
                        var sessionJson = await File.ReadAllTextAsync(_sessionFile, ct);
                        _instaApi.LoadStateDataFromString(sessionJson);
                        if (_instaApi.IsUserAuthenticated)
                        {
                            _isPrivateApiLoggedIn = true;
                        }
                    }
                    catch { }
                }

                // Login
                if (!_isPrivateApiLoggedIn)
                {
                    var loginResult = await _instaApi.LoginAsync();

                    if (loginResult.Succeeded)
                    {
                        _isPrivateApiLoggedIn = true;
                        await SaveSessionAsync();
                    }
                    else if (loginResult.Value == InstaLoginResult.TwoFactorRequired && !string.IsNullOrEmpty(TwoFactorCode))
                    {
                        var twoFactorResult = await _instaApi.TwoFactorLoginAsync(TwoFactorCode);
                        if (twoFactorResult.Succeeded)
                        {
                            _isPrivateApiLoggedIn = true;
                            await SaveSessionAsync();
                        }
                    }
                    else if (loginResult.Value == InstaLoginResult.ChallengeRequired)
                    {
                        Log.Warning("[Instagram Private] Challenge gerekli");
                        _privateApiEnabled = false;
                        return;
                    }
                    else
                    {
                        Log.Warning("[Instagram Private] Login başarısız: {Message}", loginResult.Info.Message);
                        _privateApiEnabled = false;
                        return;
                    }
                }

                // Broadcast ID bul
                _broadcastId = await FindBroadcastIdAsync(ct);

                if (string.IsNullOrEmpty(_broadcastId))
                {
                    Log.Warning("[Instagram Private] Aktif yayın bulunamadı");
                    _privateApiEnabled = false;
                    return;
                }

                Log.Information("[Instagram Private] Bağlandı (Broadcast: {Id})", _broadcastId);
                _privateApiEnabled = true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Instagram Private] Başlatma hatası");
                _privateApiEnabled = false;
            }
        }

        private async Task<string?> FindBroadcastIdAsync(CancellationToken ct)
        {
            try
            {
                var targetUser = string.IsNullOrEmpty(BroadcasterUsername) ? Username : BroadcasterUsername;

                // Kullanıcıyı bul
                var userResult = await _instaApi!.UserProcessor.GetUserAsync(targetUser);
                if (!userResult.Succeeded) return null;

                // Yayın bilgisini al
                var infoResult = await _instaApi.LiveProcessor.GetInfoAsync(userResult.Value.Pk.ToString());
                if (infoResult.Succeeded && infoResult.Value != null)
                {
                    return infoResult.Value.Id;
                }

                // Suggested broadcasts'dan ara
                var suggestedResult = await _instaApi.LiveProcessor.GetSuggestedBroadcastsAsync();
                if (suggestedResult.Succeeded && suggestedResult.Value?.Broadcasts != null)
                {
                    var broadcast = suggestedResult.Value.Broadcasts
                        .FirstOrDefault(b => b.BroadcastOwner?.UserName?.Equals(targetUser, StringComparison.OrdinalIgnoreCase) == true);
                    if (broadcast != null) return broadcast.Id;
                }

                return null;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Instagram Private] Broadcast ID bulunamadı");
                return null;
            }
        }

        private async Task InitializeGraphApiAsync(CancellationToken ct)
        {
            try
            {
                _liveMediaId = await FindLiveMediaIdGraphAsync(ct);

                if (string.IsNullOrEmpty(_liveMediaId))
                {
                    Log.Warning("[Instagram Graph] Aktif yayın bulunamadı");
                    _graphApiEnabled = false;
                    return;
                }

                Log.Information("[Instagram Graph] Bağlandı (Media: {Id})", _liveMediaId);
                _graphApiEnabled = true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Instagram Graph] Başlatma hatası");
                _graphApiEnabled = false;
            }
        }

        private async Task<string?> FindLiveMediaIdGraphAsync(CancellationToken ct)
        {
            try
            {
                var url = $"{GraphApiUrl}/me/live_media?access_token={GraphApiAccessToken}&fields=id";
                var response = await _httpClient.GetStringAsync(url, ct);

                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                {
                    return data[0].GetProperty("id").GetString();
                }
            }
            catch { }
            return null;
        }

        private async Task SaveSessionAsync()
        {
            try
            {
                var sessionJson = _instaApi!.GetStateDataAsString();
                await File.WriteAllTextAsync(_sessionFile, sessionJson);
            }
            catch { }
        }

        #endregion

        #region Message Loop

        protected override Task DisconnectAsync()
        {
            _processedComments.Clear();
            return Task.CompletedTask;
        }

        protected override async Task RunMessageLoopAsync(CancellationToken ct)
        {
            var halfInterval = TimeSpan.FromMilliseconds(TotalPollingInterval.TotalMilliseconds / 2);
            bool usePrivateNext = true;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    CleanupOldComments();

                    if (usePrivateNext && _privateApiEnabled && CanUsePrivateApi())
                    {
                        await PollPrivateApiAsync(ct);
                    }
                    else if (!usePrivateNext && _graphApiEnabled && CanUseGraphApi())
                    {
                        await PollGraphApiAsync(ct);
                    }
                    else
                    {
                        // Fallback
                        if (_graphApiEnabled && CanUseGraphApi())
                            await PollGraphApiAsync(ct);
                        else if (_privateApiEnabled && CanUsePrivateApi())
                            await PollPrivateApiAsync(ct);
                    }

                    usePrivateNext = !usePrivateNext;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Instagram Hybrid] Polling hatası");
                }

                await Task.Delay(halfInterval, ct);
            }
        }

        private bool CanUsePrivateApi()
        {
            if (!_privateApiEnabled || _instaApi == null || string.IsNullOrEmpty(_broadcastId))
                return false;
            if (DateTime.UtcNow < _privateApiDisabledUntil)
                return false;
            return true;
        }

        private bool CanUseGraphApi()
        {
            if (!_graphApiEnabled || string.IsNullOrEmpty(_liveMediaId) || string.IsNullOrEmpty(GraphApiAccessToken))
                return false;
            CheckGraphApiRateLimit();
            return _graphApiRequestsThisHour < 180;
        }

        private void CheckGraphApiRateLimit()
        {
            if ((DateTime.UtcNow - _graphApiHourStart).TotalHours >= 1)
            {
                _graphApiHourStart = DateTime.UtcNow;
                _graphApiRequestsThisHour = 0;
            }
        }

        #endregion

        #region Private API Polling

        private async Task PollPrivateApiAsync(CancellationToken ct)
        {
            try
            {
                var commentsResult = await _instaApi!.LiveProcessor.GetCommentsAsync(_broadcastId!, _lastCommentTs);

                if (!commentsResult.Succeeded)
                {
                    HandlePrivateApiError();
                    return;
                }

                _privateApiFailCount = 0;

                if (commentsResult.Value?.Comments != null)
                {
                    foreach (var comment in commentsResult.Value.Comments)
                    {
                        var commentKey = $"p_{comment.Pk}";

                        if (_processedComments.TryAdd(commentKey, 0))
                        {
                            var chatMessage = new ChatMessage
                            {
                                Platform = ChatPlatform.Instagram,
                                Username = comment.User?.UserName ?? "unknown",
                                DisplayName = comment.User?.FullName ?? comment.User?.UserName ?? "Unknown",
                                Message = comment.Text ?? "",
                                AvatarUrl = comment.User?.ProfilePicture,
                                Timestamp = comment.CreatedAtUtc,
                                Type = ChatMessageType.Normal,
                                Metadata = new Dictionary<string, string>
                                {
                                    ["source"] = "private_api",
                                    ["comment_pk"] = comment.Pk.ToString()
                                }
                            };
                            PublishMessage(chatMessage);
                        }

                        // Son timestamp'i güncelle
                        var ts = comment.CreatedAtUtc.Ticks.ToString();
                        if (string.Compare(ts, _lastCommentTs) > 0)
                            _lastCommentTs = ts;
                    }
                }

                // Heartbeat
                var heartbeat = await _instaApi.LiveProcessor.GetHeartBeatAndViewerCountAsync(_broadcastId!);
                if (!heartbeat.Succeeded || heartbeat.Value?.BroadcastStatus == "stopped")
                {
                    Log.Information("[Instagram Private] Yayın sona erdi");
                    _privateApiEnabled = false;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[Instagram Private] Poll hatası");
                HandlePrivateApiError();
            }
        }

        private void HandlePrivateApiError()
        {
            _privateApiFailCount++;
            if (_privateApiFailCount >= 3)
            {
                _privateApiDisabledUntil = DateTime.UtcNow.AddMinutes(5);
                Log.Warning("[Instagram Private] 3 hata, 5 dakika devre dışı");
            }
        }

        #endregion

        #region Graph API Polling

        private async Task PollGraphApiAsync(CancellationToken ct)
        {
            try
            {
                var url = $"{GraphApiUrl}/{_liveMediaId}/comments" +
                         $"?access_token={GraphApiAccessToken}" +
                         $"&fields=id,text,from{{id,username}},timestamp" +
                         $"&limit=50";

                if (!string.IsNullOrEmpty(_lastGraphCommentCursor))
                    url += $"&after={_lastGraphCommentCursor}";

                using var response = await _httpClient.GetAsync(url, ct);
                _graphApiRequestsThisHour++;

                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode == 429)
                        _graphApiRequestsThisHour = 200;
                    return;
                }

                var content = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(content);

                if (doc.RootElement.TryGetProperty("data", out var comments))
                {
                    foreach (var comment in comments.EnumerateArray())
                    {
                        var commentId = comment.GetProperty("id").GetString() ?? "";
                        var commentKey = $"g_{commentId}";

                        if (_processedComments.TryAdd(commentKey, 0))
                        {
                            string? username = null;
                            string? text = null;

                            if (comment.TryGetProperty("from", out var from))
                                username = from.TryGetProperty("username", out var u) ? u.GetString() : null;
                            if (comment.TryGetProperty("text", out var t))
                                text = t.GetString();

                            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(text))
                            {
                                var chatMessage = new ChatMessage
                                {
                                    Platform = ChatPlatform.Instagram,
                                    Username = username,
                                    DisplayName = username,
                                    Message = text,
                                    Timestamp = DateTime.UtcNow,
                                    Type = ChatMessageType.Normal,
                                    Metadata = new Dictionary<string, string> { ["source"] = "graph_api" }
                                };
                                PublishMessage(chatMessage);
                            }
                        }
                    }
                }

                if (doc.RootElement.TryGetProperty("paging", out var paging) &&
                    paging.TryGetProperty("cursors", out var cursors) &&
                    cursors.TryGetProperty("after", out var after))
                {
                    _lastGraphCommentCursor = after.GetString();
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[Instagram Graph] Poll hatası");
            }
        }

        #endregion

        #region Cleanup & Dispose

        private void CleanupOldComments()
        {
            if ((DateTime.UtcNow - _lastCleanup).TotalMinutes < 5) return;
            _lastCleanup = DateTime.UtcNow;
            if (_processedComments.Count > 5000)
                _processedComments.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _httpClient.Dispose();
            base.Dispose(disposing);
        }

        #endregion
    }

    internal class InstaApiSerilogLogger : IInstaLogger
    {
        public void LogRequest(HttpRequestMessage request) => Log.Verbose("[IG] {Method} {Uri}", request.Method, request.RequestUri);
        public void LogResponse(HttpResponseMessage response) => Log.Verbose("[IG] {Status}", response.StatusCode);
        public void LogException(Exception ex) => Log.Debug(ex, "[IG] Exception");
        public void LogRequest(Uri uri) => Log.Verbose("[IG] {Uri}", uri);
        public void LogInfo(string info) => Log.Debug("[IG] {Info}", info);
    }
}