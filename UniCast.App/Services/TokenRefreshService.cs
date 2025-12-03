using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using UniCast.Core.Chat;

namespace UniCast.App.Services
{
    /// <summary>
    /// DÜZELTME v18: Platform token refresh servisi
    /// OAuth token'ları otomatik olarak yeniler
    /// </summary>
    public sealed class TokenRefreshService : IDisposable
    {
        #region Singleton

        private static readonly Lazy<TokenRefreshService> _instance =
            new(() => new TokenRefreshService(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static TokenRefreshService Instance => _instance.Value;

        #endregion

        #region Fields

        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<ChatPlatform, TokenInfo> _tokens = new();
        private readonly System.Threading.Timer _refreshTimer;
        private bool _disposed;

        private static class RefreshConfig
        {
            public const int CheckIntervalMinutes = 5;
            public const int RefreshBeforeExpiryMinutes = 10;
            public const int MaxRetries = 3;
        }

        #endregion

        #region Events

        /// <summary>
        /// Token yenilendiğinde tetiklenir
        /// </summary>
        public event EventHandler<TokenRefreshedEventArgs>? OnTokenRefreshed;

        /// <summary>
        /// Token yenileme başarısız olduğunda tetiklenir
        /// </summary>
        public event EventHandler<TokenRefreshFailedEventArgs>? OnTokenRefreshFailed;

        /// <summary>
        /// Token expire olmak üzereyken tetiklenir
        /// </summary>
        public event EventHandler<TokenExpiringEventArgs>? OnTokenExpiring;

        #endregion

        #region Constructor

        private TokenRefreshService()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            // Periyodik kontrol timer'ı
            _refreshTimer = new System.Threading.Timer(
                CheckTokensCallback,
                null,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(RefreshConfig.CheckIntervalMinutes));
        }

        #endregion

        #region Token Registration

        /// <summary>
        /// Token kaydet veya güncelle
        /// </summary>
        public void RegisterToken(ChatPlatform platform, TokenInfo token)
        {
            _tokens[platform] = token;
            Log.Information("[TokenRefresh] {Platform} token kaydedildi, bitiş: {Expiry}",
                platform, token.ExpiresAt);
        }

        /// <summary>
        /// Token'ı kaldır
        /// </summary>
        public void UnregisterToken(ChatPlatform platform)
        {
            _tokens.TryRemove(platform, out _);
            Log.Debug("[TokenRefresh] {Platform} token kaldırıldı", platform);
        }

        /// <summary>
        /// Mevcut token'ı al
        /// </summary>
        public TokenInfo? GetToken(ChatPlatform platform)
        {
            return _tokens.TryGetValue(platform, out var token) ? token : null;
        }

        /// <summary>
        /// Token geçerli mi kontrol et
        /// </summary>
        public bool IsTokenValid(ChatPlatform platform)
        {
            if (!_tokens.TryGetValue(platform, out var token))
                return false;

            return token.ExpiresAt > DateTime.UtcNow;
        }

        #endregion

        #region Token Refresh

        /// <summary>
        /// Timer callback - tüm token'ları kontrol et
        /// </summary>
        private void CheckTokensCallback(object? state)
        {
            if (_disposed) return;

            foreach (var kvp in _tokens)
            {
                try
                {
                    CheckAndRefreshToken(kvp.Key, kvp.Value);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[TokenRefresh] {Platform} token kontrolü hatası", kvp.Key);
                }
            }
        }

        /// <summary>
        /// Tek bir token'ı kontrol et ve gerekirse yenile
        /// </summary>
        private void CheckAndRefreshToken(ChatPlatform platform, TokenInfo token)
        {
            var timeUntilExpiry = token.ExpiresAt - DateTime.UtcNow;

            // Zaten expired
            if (timeUntilExpiry <= TimeSpan.Zero)
            {
                Log.Warning("[TokenRefresh] {Platform} token expired!", platform);
                OnTokenExpiring?.Invoke(this, new TokenExpiringEventArgs
                {
                    Platform = platform,
                    ExpiresAt = token.ExpiresAt,
                    IsExpired = true
                });
                return;
            }

            // Yakında expire olacak
            if (timeUntilExpiry <= TimeSpan.FromMinutes(RefreshConfig.RefreshBeforeExpiryMinutes))
            {
                OnTokenExpiring?.Invoke(this, new TokenExpiringEventArgs
                {
                    Platform = platform,
                    ExpiresAt = token.ExpiresAt,
                    IsExpired = false
                });

                // Otomatik refresh (refresh token varsa)
                if (!string.IsNullOrEmpty(token.RefreshToken))
                {
                    _ = RefreshTokenAsync(platform, token);
                }
            }
        }

        /// <summary>
        /// Token'ı yenile
        /// </summary>
        public async Task<bool> RefreshTokenAsync(ChatPlatform platform, TokenInfo? currentToken = null)
        {
            currentToken ??= GetToken(platform);
            if (currentToken == null || string.IsNullOrEmpty(currentToken.RefreshToken))
            {
                Log.Warning("[TokenRefresh] {Platform} refresh token yok", platform);
                return false;
            }

            for (int attempt = 1; attempt <= RefreshConfig.MaxRetries; attempt++)
            {
                try
                {
                    var newToken = platform switch
                    {
                        ChatPlatform.Twitch => await RefreshTwitchTokenAsync(currentToken),
                        ChatPlatform.YouTube => await RefreshYouTubeTokenAsync(currentToken),
                        ChatPlatform.Facebook => await RefreshFacebookTokenAsync(currentToken),
                        _ => null
                    };

                    if (newToken != null)
                    {
                        _tokens[platform] = newToken;

                        Log.Information("[TokenRefresh] {Platform} token yenilendi, yeni bitiş: {Expiry}",
                            platform, newToken.ExpiresAt);

                        OnTokenRefreshed?.Invoke(this, new TokenRefreshedEventArgs
                        {
                            Platform = platform,
                            NewToken = newToken
                        });

                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[TokenRefresh] {Platform} refresh denemesi {Attempt} başarısız",
                        platform, attempt);

                    if (attempt < RefreshConfig.MaxRetries)
                    {
                        await Task.Delay(1000 * attempt);
                    }
                }
            }

            Log.Error("[TokenRefresh] {Platform} token yenileme başarısız", platform);

            OnTokenRefreshFailed?.Invoke(this, new TokenRefreshFailedEventArgs
            {
                Platform = platform,
                Reason = "Token refresh failed after multiple attempts"
            });

            return false;
        }

        #endregion

        #region Platform-Specific Refresh

        private async Task<TokenInfo?> RefreshTwitchTokenAsync(TokenInfo currentToken)
        {
            // Twitch OAuth refresh
            // https://dev.twitch.tv/docs/authentication/refresh-tokens/

            var clientId = Environment.GetEnvironmentVariable("TWITCH_CLIENT_ID");
            var clientSecret = Environment.GetEnvironmentVariable("TWITCH_CLIENT_SECRET");

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                Log.Warning("[TokenRefresh] Twitch client credentials not configured");
                return null;
            }

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = currentToken.RefreshToken!,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret
            });

            var response = await _httpClient.PostAsync("https://id.twitch.tv/oauth2/token", content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Error("[TokenRefresh] Twitch refresh failed: {Error}", error);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            return new TokenInfo
            {
                Platform = ChatPlatform.Twitch,
                AccessToken = doc.RootElement.GetProperty("access_token").GetString()!,
                RefreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt)
                    ? rt.GetString()
                    : currentToken.RefreshToken,
                ExpiresAt = DateTime.UtcNow.AddSeconds(
                    doc.RootElement.GetProperty("expires_in").GetInt32()),
                TokenType = "Bearer"
            };
        }

        private async Task<TokenInfo?> RefreshYouTubeTokenAsync(TokenInfo currentToken)
        {
            // YouTube/Google OAuth refresh
            var clientId = Environment.GetEnvironmentVariable("YOUTUBE_CLIENT_ID");
            var clientSecret = Environment.GetEnvironmentVariable("YOUTUBE_CLIENT_SECRET");

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                Log.Warning("[TokenRefresh] YouTube client credentials not configured");
                return null;
            }

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = currentToken.RefreshToken!,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret
            });

            var response = await _httpClient.PostAsync("https://oauth2.googleapis.com/token", content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Error("[TokenRefresh] YouTube refresh failed: {Error}", error);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            return new TokenInfo
            {
                Platform = ChatPlatform.YouTube,
                AccessToken = doc.RootElement.GetProperty("access_token").GetString()!,
                RefreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt)
                    ? rt.GetString()
                    : currentToken.RefreshToken,
                ExpiresAt = DateTime.UtcNow.AddSeconds(
                    doc.RootElement.GetProperty("expires_in").GetInt32()),
                TokenType = "Bearer"
            };
        }

        private async Task<TokenInfo?> RefreshFacebookTokenAsync(TokenInfo currentToken)
        {
            // Facebook long-lived token exchange
            // Note: Facebook doesn't have traditional refresh tokens
            // Instead, exchange for a new long-lived token

            var appId = Environment.GetEnvironmentVariable("FACEBOOK_APP_ID");
            var appSecret = Environment.GetEnvironmentVariable("FACEBOOK_APP_SECRET");

            if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(appSecret))
            {
                Log.Warning("[TokenRefresh] Facebook app credentials not configured");
                return null;
            }

            var url = $"https://graph.facebook.com/v18.0/oauth/access_token" +
                      $"?grant_type=fb_exchange_token" +
                      $"&client_id={appId}" +
                      $"&client_secret={appSecret}" +
                      $"&fb_exchange_token={currentToken.AccessToken}";

            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Error("[TokenRefresh] Facebook refresh failed: {Error}", error);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            return new TokenInfo
            {
                Platform = ChatPlatform.Facebook,
                AccessToken = doc.RootElement.GetProperty("access_token").GetString()!,
                ExpiresAt = doc.RootElement.TryGetProperty("expires_in", out var exp)
                    ? DateTime.UtcNow.AddSeconds(exp.GetInt32())
                    : DateTime.UtcNow.AddDays(60), // Long-lived tokens last ~60 days
                TokenType = "Bearer"
            };
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _refreshTimer.Dispose();
            _httpClient.Dispose();
            _tokens.Clear();

            OnTokenRefreshed = null;
            OnTokenRefreshFailed = null;
            OnTokenExpiring = null;
        }

        #endregion
    }

    #region Token Types

    /// <summary>
    /// Token bilgileri
    /// </summary>
    public class TokenInfo
    {
        public ChatPlatform Platform { get; init; }
        public string AccessToken { get; init; } = "";
        public string? RefreshToken { get; init; }
        public DateTime ExpiresAt { get; init; }
        public string TokenType { get; init; } = "Bearer";
        public Dictionary<string, string> AdditionalData { get; init; } = new();

        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
        public TimeSpan TimeUntilExpiry => ExpiresAt - DateTime.UtcNow;
    }

    /// <summary>
    /// Token yenilendi event argümanları
    /// </summary>
    public class TokenRefreshedEventArgs : EventArgs
    {
        public ChatPlatform Platform { get; init; }
        public TokenInfo NewToken { get; init; } = null!;
    }

    /// <summary>
    /// Token yenileme başarısız event argümanları
    /// </summary>
    public class TokenRefreshFailedEventArgs : EventArgs
    {
        public ChatPlatform Platform { get; init; }
        public string Reason { get; init; } = "";
    }

    /// <summary>
    /// Token expire olmak üzere event argümanları
    /// </summary>
    public class TokenExpiringEventArgs : EventArgs
    {
        public ChatPlatform Platform { get; init; }
        public DateTime ExpiresAt { get; init; }
        public bool IsExpired { get; init; }
    }

    #endregion
}
