using System;

namespace UniCast.App
{
    /// <summary>
    /// DÜZELTME v20: Merkezi sabitler
    /// Tüm magic number'lar burada tanımlanmalı
    /// </summary>
    public static class AppConstants
    {
        #region Timeouts (Milliseconds)

        public static class Timeouts
        {
            /// <summary>HTTP istek timeout'u</summary>
            public const int HttpRequestMs = 30000;

            /// <summary>WebSocket bağlantı timeout'u</summary>
            public const int WebSocketConnectMs = 15000;

            /// <summary>Stream başlatma timeout'u</summary>
            public const int StreamStartMs = 10000;

            /// <summary>Stream durdurma timeout'u</summary>
            public const int StreamStopMs = 5000;

            /// <summary>FFmpeg process timeout'u</summary>
            public const int FfmpegProcessMs = 30000;

            /// <summary>Overlay pipe bağlantı timeout'u</summary>
            public const int OverlayPipeConnectMs = 5000;

            /// <summary>Named pipe bağlantı bekleme timeout'u</summary>
            public const int PipeConnectionMs = 30000;

            /// <summary>License doğrulama timeout'u</summary>
            public const int LicenseValidationMs = 10000;

            /// <summary>Token refresh timeout'u</summary>
            public const int TokenRefreshMs = 15000;

            /// <summary>Health check timeout'u</summary>
            public const int HealthCheckMs = 5000;

            /// <summary>Graceful shutdown timeout'u</summary>
            public const int GracefulShutdownMs = 10000;

            /// <summary>Chat polling interval'ı</summary>
            public const int ChatPollingMs = 1000;

            /// <summary>Retry başlangıç delay'i</summary>
            public const int RetryInitialMs = 100;

            /// <summary>Retry maksimum delay'i</summary>
            public const int RetryMaxMs = 30000;
        }

        #endregion

        #region Intervals (Seconds/Minutes/Milliseconds)

        public static class Intervals
        {
            /// <summary>Memory sampling interval'ı (saniye)</summary>
            public const int MemorySamplingSeconds = 5;

            /// <summary>Performance sampling interval'ı (saniye)</summary>
            public const int PerformanceSamplingSeconds = 1;

            /// <summary>Health check interval'ı (saniye)</summary>
            public const int HealthCheckSeconds = 30;

            /// <summary>Token refresh kontrolü (dakika)</summary>
            public const int TokenRefreshCheckMinutes = 5;

            /// <summary>Token yenileme (dakika - expire'dan önce)</summary>
            public const int TokenRefreshBeforeExpiryMinutes = 10;

            /// <summary>Auto-update kontrol interval'ı (saat)</summary>
            public const int AutoUpdateCheckHours = 6;

            /// <summary>Metrics flush interval'ı (dakika)</summary>
            public const int MetricsFlushMinutes = 1;

            /// <summary>Resource cleanup interval'ı (dakika)</summary>
            public const int ResourceCleanupMinutes = 1;

            /// <summary>Status bar güncelleme interval'ı (saniye)</summary>
            public const int StatusUpdateSeconds = 1;

            /// <summary>Genel timer tick interval'ı (saniye) - Uptime, Break timer vb.</summary>
            public const int TimerTickSeconds = 1;

            /// <summary>Overlay frame gönderim delay'i (milisaniye)</summary>
            public const int OverlayFrameDelayMs = 10;

            /// <summary>Chat batch işleme interval'ı (milisaniye)</summary>
            public const int ChatBatchProcessMs = 250;

            /// <summary>Chat polling interval'ı (saniye)</summary>
            public const int ChatPollingSeconds = 4;
        }

        #endregion

        #region Limits

        public static class Limits
        {
            /// <summary>Maksimum retry sayısı</summary>
            public const int MaxRetries = 3;

            /// <summary>Chat mesaj buffer boyutu</summary>
            public const int ChatMessageBufferSize = 100;

            /// <summary>Log dosyası boyut limiti (byte)</summary>
            public const long LogFileSizeBytes = 50 * 1024 * 1024; // 50 MB

            /// <summary>Log dosyası sayısı limiti</summary>
            public const int LogFileRetainedCount = 7;

            /// <summary>Memory warning eşiği (MB)</summary>
            public const long MemoryWarningMB = 500;

            /// <summary>Memory critical eşiği (MB)</summary>
            public const long MemoryCriticalMB = 1000;

            /// <summary>CPU warning eşiği (%)</summary>
            public const double CpuWarningPercent = 80.0;

            /// <summary>CPU critical eşiği (%)</summary>
            public const double CpuCriticalPercent = 95.0;

            /// <summary>Disk warning eşiği (GB)</summary>
            public const long DiskWarningGB = 5;

            /// <summary>YouTube günlük quota limiti</summary>
            public const int YouTubeQuotaDaily = 10000;

            /// <summary>Object pool maksimum boyutu</summary>
            public const int ObjectPoolMaxSize = 100;

            /// <summary>Memory sample geçmişi</summary>
            public const int MemorySampleHistory = 720; // 1 saat @ 5sn

            /// <summary>Performance sample geçmişi</summary>
            public const int PerformanceSampleHistory = 300; // 5 dakika @ 1sn

            /// <summary>UI chat view maksimum mesaj sayısı</summary>
            public const int MaxUiChatMessages = 500;

            /// <summary>Overlay chat maksimum mesaj sayısı</summary>
            public const int MaxOverlayChatMessages = 30;
        }

        #endregion

        #region Video Settings

        public static class Video
        {
            /// <summary>Minimum genişlik</summary>
            public const int MinWidth = 640;

            /// <summary>Maksimum genişlik</summary>
            public const int MaxWidth = 3840;

            /// <summary>Minimum yükseklik</summary>
            public const int MinHeight = 360;

            /// <summary>Maksimum yükseklik</summary>
            public const int MaxHeight = 2160;

            /// <summary>Minimum FPS</summary>
            public const int MinFps = 15;

            /// <summary>Maksimum FPS</summary>
            public const int MaxFps = 60;

            /// <summary>Varsayılan FPS</summary>
            public const int DefaultFps = 30;

            /// <summary>Minimum video bitrate (kbps)</summary>
            public const int MinBitrateKbps = 500;

            /// <summary>Maksimum video bitrate (kbps)</summary>
            public const int MaxBitrateKbps = 50000;

            /// <summary>Varsayılan video bitrate (kbps)</summary>
            public const int DefaultBitrateKbps = 4500;
        }

        #endregion

        #region Audio Settings

        public static class Audio
        {
            /// <summary>Geçerli bitrate değerleri (kbps)</summary>
            public static readonly int[] ValidBitrates = { 64, 96, 128, 160, 192, 256, 320 };

            /// <summary>Varsayılan audio bitrate (kbps)</summary>
            public const int DefaultBitrateKbps = 128;

            /// <summary>Geçerli sample rate değerleri</summary>
            public static readonly int[] ValidSampleRates = { 44100, 48000 };

            /// <summary>Varsayılan sample rate</summary>
            public const int DefaultSampleRate = 48000;

            /// <summary>Minimum kanal sayısı</summary>
            public const int MinChannels = 1;

            /// <summary>Maksimum kanal sayısı</summary>
            public const int MaxChannels = 2;
        }

        #endregion

        #region UI Constants

        public static class UI
        {
            /// <summary>Toast mesaj gösterim süresi (saniye)</summary>
            public const int ToastDurationSeconds = 3;

            /// <summary>Save confirmation gösterim süresi (saniye)</summary>
            public const int SaveConfirmationSeconds = 3;

            /// <summary>Platform status güncelleme interval'ı (saniye)</summary>
            public const int PlatformStatusUpdateSeconds = 1;

            /// <summary>Preview FPS</summary>
            public const int PreviewFps = 30;

            /// <summary>Overlay maksimum mesaj sayısı</summary>
            public const int OverlayMaxMessages = 50;

            /// <summary>Chat view maksimum mesaj sayısı</summary>
            public const int ChatViewMaxMessages = 500;
        }

        #endregion

        #region Buffer Sizes

        public static class Buffers
        {
            /// <summary>Named pipe buffer boyutu</summary>
            public const int NamedPipeBufferSize = 65536;

            /// <summary>Stream read buffer boyutu</summary>
            public const int StreamReadBufferSize = 8192;

            /// <summary>File read buffer boyutu</summary>
            public const int FileReadBufferSize = 4096;

            /// <summary>WebSocket receive buffer boyutu</summary>
            public const int WebSocketReceiveBufferSize = 8192;

            /// <summary>HTTP response buffer boyutu</summary>
            public const int HttpResponseBufferSize = 81920;
        }

        #endregion

        #region Paths

        public static class Paths
        {
            /// <summary>Uygulama klasörü adı</summary>
            public const string AppFolderName = "UniCast";

            /// <summary>Log klasörü adı</summary>
            public const string LogFolderName = "Logs";

            /// <summary>Update klasörü adı</summary>
            public const string UpdateFolderName = "Updates";

            /// <summary>Cache klasörü adı</summary>
            public const string CacheFolderName = "Cache";

            /// <summary>Ayarlar dosyası adı</summary>
            public const string SettingsFileName = "settings.json";

            /// <summary>Hedefler dosyası adı</summary>
            public const string TargetsFileName = "targets.json";

            /// <summary>Log level dosyası adı</summary>
            public const string LogLevelFileName = "loglevel.txt";

            /// <summary>Crash marker dosyası adı</summary>
            public const string CrashMarkerFileName = "crash_marker.tmp";
        }

        #endregion

        #region API Quotas

        public static class Quotas
        {
            /// <summary>YouTube videos.list quota maliyeti</summary>
            public const int YouTubeVideosListCost = 1;

            /// <summary>YouTube liveChat.messages quota maliyeti</summary>
            public const int YouTubeLiveChatMessagesCost = 5;

            /// <summary>Quota warning eşiği (%)</summary>
            public const int QuotaWarningPercent = 80;

            /// <summary>Quota critical eşiği (%)</summary>
            public const int QuotaCriticalPercent = 95;
        }

        #endregion

        #region Rate Limiting

        public static class RateLimits
        {
            /// <summary>Günlük maksimum aktivasyon denemesi</summary>
            public const int DailyActivationLimit = 10;

            /// <summary>Günlük maksimum deaktivasyon denemesi</summary>
            public const int DailyDeactivationLimit = 5;

            /// <summary>Saatlik maksimum validation denemesi</summary>
            public const int HourlyValidationLimit = 100;

            /// <summary>Şüpheli aktivite eşiği (günlük istek)</summary>
            public const int SuspiciousActivityThreshold = 50;
        }

        #endregion

        #region Retry Settings

        public static class Retry
        {
            /// <summary>Varsayılan maksimum retry</summary>
            public const int DefaultMaxRetries = 3;

            /// <summary>HTTP için maksimum retry</summary>
            public const int HttpMaxRetries = 3;

            /// <summary>Database için maksimum retry</summary>
            public const int DatabaseMaxRetries = 5;

            /// <summary>Backoff çarpanı</summary>
            public const double BackoffMultiplier = 2.0;

            /// <summary>Jitter faktörü</summary>
            public const double JitterFactor = 0.1;
        }

        #endregion
    }
}