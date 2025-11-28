using System;
using System.Net.Http;

namespace UniCast.App.Services
{
    /// <summary>
    /// Uygulama genelinde tek HttpClient instance'ı sağlar.
    /// Her seferinde new HttpClient() yapmak socket exhaustion'a yol açar.
    /// </summary>
    public static class HttpClientFactory
    {
        private static readonly Lazy<HttpClient> _defaultClient = new(() =>
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 10
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            client.DefaultRequestHeaders.Add("User-Agent", "UniCast/1.0");

            return client;
        });

        /// <summary>
        /// Genel amaçlı HTTP istekleri için kullanın.
        /// Bu instance'ı DISPOSE ETMEYİN!
        /// </summary>
        public static HttpClient Default => _defaultClient.Value;

        private static readonly Lazy<HttpClient> _tiktokClient = new(() =>
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 5
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };

            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            return client;
        });

        /// <summary>
        /// TikTok API istekleri için özel client (farklı User-Agent).
        /// </summary>
        public static HttpClient TikTok => _tiktokClient.Value;

        private static readonly Lazy<HttpClient> _instagramClient = new(() =>
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 5
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };

            client.DefaultRequestHeaders.Add("User-Agent", "Instagram 287.0.0.27.109 Android");
            client.DefaultRequestHeaders.Add("X-IG-App-ID", "567067343352427");

            return client;
        });

        /// <summary>
        /// Instagram API istekleri için özel client.
        /// </summary>
        public static HttpClient Instagram => _instagramClient.Value;
    }
}