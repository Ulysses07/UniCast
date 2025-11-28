using System;

namespace UniCast.App
{
    /// <summary>
    /// Uygulama genelinde kullanılan sabitler.
    /// Magic number'ları buraya taşıyarak merkezi yönetim sağlanır.
    /// </summary>
    public static class Constants
    {
        /// <summary>
        /// Uygulama bilgileri
        /// </summary>
        public static class App
        {
            public const string Name = "UniCast";
            public const string Version = "1.0.0";
            public const string Publisher = "UniCast Studio";
        }

        /// <summary>
        /// Önizleme ayarları
        /// </summary>
        public static class Preview
        {
            /// <summary>Frame aralığı (ms) - ~30 FPS</summary>
            public const int FrameIntervalMs = 33;

            /// <summary>Varsayılan genişlik</summary>
            public const int DefaultWidth = 1280;

            /// <summary>Varsayılan yükseklik</summary>
            public const int DefaultHeight = 720;

            /// <summary>Varsayılan FPS</summary>
            public const int DefaultFps = 30;
        }

        /// <summary>
        /// Chat sistemi ayarları
        /// </summary>
        public static class Chat
        {
            /// <summary>Saniyede maksimum mesaj (rate limit)</summary>
            public const int MaxMessagesPerSecond = 20;

            /// <summary>Dedupe cache kapasitesi</summary>
            public const int CacheCapacity = 10000;

            /// <summary>Overlay'de gösterilecek maksimum mesaj</summary>
            public const int MaxOverlayMessages = 8;

            /// <summary>UI'da gösterilecek maksimum mesaj</summary>
            public const int MaxUiMessages = 1000;

            /// <summary>İstatistik loglama aralığı (ms)</summary>
            public const int StatsIntervalMs = 60000;
        }

        /// <summary>
        /// Yeniden bağlanma ayarları
        /// </summary>
        public static class Reconnect
        {
            /// <summary>Maksimum deneme sayısı</summary>
            public const int MaxAttempts = 5;

            /// <summary>Başlangıç bekleme süresi (ms)</summary>
            public const int InitialDelayMs = 1000;

            /// <summary>Maksimum bekleme süresi (ms)</summary>
            public const int MaxDelayMs = 5000;

            /// <summary>Bağlantı timeout (saniye)</summary>
            public const int TimeoutSeconds = 15;
        }

        /// <summary>
        /// Overlay ayarları
        /// </summary>
        public static class Overlay
        {
            /// <summary>Minimum genişlik</summary>
            public const int MinWidth = 200;

            /// <summary>Minimum yükseklik</summary>
            public const int MinHeight = 100;

            /// <summary>Maksimum genişlik</summary>
            public const int MaxWidth = 1920;

            /// <summary>Maksimum yükseklik</summary>
            public const int MaxHeight = 1080;

            /// <summary>Varsayılan opacity</summary>
            public const double DefaultOpacity = 0.85;

            /// <summary>Minimum render aralığı (ms) - ~30 FPS</summary>
            public const int MinRenderIntervalMs = 33;

            /// <summary>Named pipe maksimum reconnect</summary>
            public const int MaxPipeReconnectAttempts = 10;

            /// <summary>Pipe reconnect bekleme (ms)</summary>
            public const int PipeReconnectDelayMs = 1000;
        }

        /// <summary>
        /// FFmpeg ayarları
        /// </summary>
        public static class FFmpeg
        {
            /// <summary>Graceful shutdown timeout (ms)</summary>
            public const int GracefulTimeoutMs = 3000;

            /// <summary>Kill timeout (ms)</summary>
            public const int KillTimeoutMs = 2000;

            /// <summary>Log okuma buffer boyutu</summary>
            public const int LogBufferSize = 2048;
        }

        /// <summary>
        /// Dosya işlemleri ayarları
        /// </summary>
        public static class Storage
        {
            /// <summary>Yazma retry sayısı</summary>
            public const int MaxRetryAttempts = 3;

            /// <summary>Retry bekleme süresi (ms)</summary>
            public const int RetryDelayMs = 100;

            /// <summary>Log dosyası saklama süresi (gün)</summary>
            public const int LogRetentionDays = 7;
        }

        /// <summary>
        /// HTTP ayarları
        /// </summary>
        public static class Http
        {
            /// <summary>Varsayılan timeout (saniye)</summary>
            public const int DefaultTimeoutSeconds = 30;

            /// <summary>Bağlantı havuzu ömrü (dakika)</summary>
            public const int PooledConnectionLifetimeMinutes = 5;

            /// <summary>Boşta bağlantı timeout (dakika)</summary>
            public const int IdleConnectionTimeoutMinutes = 2;

            /// <summary>Sunucu başına maksimum bağlantı</summary>
            public const int MaxConnectionsPerServer = 10;
        }

        /// <summary>
        /// Yayın kalitesi varsayılanları
        /// </summary>
        public static class StreamQuality
        {
            public const int DefaultVideoBitrateKbps = 3500;
            public const int DefaultAudioBitrateKbps = 128;
            public const int DefaultFps = 30;
            public const int DefaultWidth = 1280;
            public const int DefaultHeight = 720;
            public const string DefaultPreset = "veryfast";
        }

        /// <summary>
        /// Timeout değerleri (TimeSpan olarak)
        /// </summary>
        public static class Timeouts
        {
            public static readonly TimeSpan HttpDefault = TimeSpan.FromSeconds(Http.DefaultTimeoutSeconds);
            public static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(Constants.Reconnect.TimeoutSeconds);
            public static readonly TimeSpan GracefulShutdown = TimeSpan.FromMilliseconds(FFmpeg.GracefulTimeoutMs);
        }
    }
}