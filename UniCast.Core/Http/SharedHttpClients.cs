using System;
using System.Net.Http;

namespace UniCast.Core.Http
{
    /// <summary>
    /// Uygulama genelinde paylaşılan HttpClient instance'ları.
    /// 
    /// ÖNEMLİ: Bu client'ları DISPOSE ETMEYİN!
    /// Her seferinde new HttpClient() yapmak socket exhaustion'a yol açar.
    /// 
    /// Referans: https://docs.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines
    /// </summary>
    public static class SharedHttpClients
    {
        #region Lazy Initialization

        private static readonly Lazy<HttpClient> _defaultClient = new(() => CreateClient(
            userAgent: "UniCast/1.0",
            timeout: TimeSpan.FromSeconds(30)));

        private static readonly Lazy<HttpClient> _tiktokClient = new(() => CreateClient(
            userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            timeout: TimeSpan.FromSeconds(15),
            additionalHeaders: new[] { ("Accept", "application/json") }));

        private static readonly Lazy<HttpClient> _instagramClient = new(() => CreateClient(
            userAgent: "Instagram 287.0.0.27.109 Android",
            timeout: TimeSpan.FromSeconds(15),
            additionalHeaders: new[] { ("X-IG-App-ID", "567067343352427") }));

        private static readonly Lazy<HttpClient> _facebookClient = new(() => CreateClient(
            userAgent: "UniCast/1.0",
            timeout: TimeSpan.FromMinutes(5), // SSE için uzun timeout
            additionalHeaders: new[] { ("Accept", "text/event-stream"), ("Cache-Control", "no-cache") }));

        private static readonly Lazy<HttpClient> _graphApiClient = new(() => CreateClient(
            userAgent: "UniCast-GraphAPI/1.0",
            timeout: TimeSpan.FromSeconds(30)));

        #endregion

        #region Public Properties

        /// <summary>
        /// Genel amaçlı HTTP istekleri için.
        /// </summary>
        public static HttpClient Default => _defaultClient.Value;

        /// <summary>
        /// TikTok API istekleri için (browser User-Agent ile).
        /// </summary>
        public static HttpClient TikTok => _tiktokClient.Value;

        /// <summary>
        /// Instagram Private API istekleri için.
        /// </summary>
        public static HttpClient Instagram => _instagramClient.Value;

        /// <summary>
        /// Facebook SSE (Server-Sent Events) için.
        /// </summary>
        public static HttpClient Facebook => _facebookClient.Value;

        /// <summary>
        /// Facebook/Instagram Graph API için.
        /// </summary>
        public static HttpClient GraphApi => _graphApiClient.Value;

        #endregion

        #region Factory

        private static HttpClient CreateClient(
            string userAgent,
            TimeSpan timeout,
            (string Key, string Value)[]? additionalHeaders = null)
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 10,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                EnableMultipleHttp2Connections = true
            };

            var client = new HttpClient(handler)
            {
                Timeout = timeout
            };

            client.DefaultRequestHeaders.Add("User-Agent", userAgent);

            if (additionalHeaders != null)
            {
                foreach (var (key, value) in additionalHeaders)
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
                }
            }

            return client;
        }

        #endregion
    }
}