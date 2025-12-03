using System;
using System.Collections.Generic;

namespace UniCast.App.Resources
{
    /// <summary>
    /// DÜZELTME v18: Merkezi hata mesajları yönetimi
    /// Gelecekte .resx dosyalarına taşınabilir
    /// </summary>
    public static class ErrorStrings
    {
        #region Current Language

        private static string _currentLanguage = "tr";

        /// <summary>
        /// Mevcut dil kodu (tr, en)
        /// </summary>
        public static string CurrentLanguage
        {
            get => _currentLanguage;
            set
            {
                if (_currentLanguage != value && SupportedLanguages.Contains(value))
                {
                    _currentLanguage = value;
                    LanguageChanged?.Invoke(null, value);
                }
            }
        }

        public static event EventHandler<string>? LanguageChanged;

        public static readonly HashSet<string> SupportedLanguages = new() { "tr", "en" };

        #endregion

        #region Connection Errors

        public static string ConnectionFailed => CurrentLanguage switch
        {
            "en" => "Connection failed",
            _ => "Bağlantı başarısız"
        };

        public static string ConnectionTimeout => CurrentLanguage switch
        {
            "en" => "Connection timed out",
            _ => "Bağlantı zaman aşımına uğradı"
        };

        public static string Reconnecting => CurrentLanguage switch
        {
            "en" => "Reconnecting...",
            _ => "Yeniden bağlanılıyor..."
        };

        public static string Disconnected => CurrentLanguage switch
        {
            "en" => "Disconnected",
            _ => "Bağlantı kesildi"
        };

        public static string Connected => CurrentLanguage switch
        {
            "en" => "Connected",
            _ => "Bağlandı"
        };

        #endregion

        #region Stream Errors

        public static string StreamStartFailed => CurrentLanguage switch
        {
            "en" => "Failed to start stream",
            _ => "Yayın başlatılamadı"
        };

        public static string StreamStopFailed => CurrentLanguage switch
        {
            "en" => "Failed to stop stream",
            _ => "Yayın durdurulamadı"
        };

        public static string NoTargetsConfigured => CurrentLanguage switch
        {
            "en" => "No streaming targets configured",
            _ => "Yayın hedefi yapılandırılmamış"
        };

        public static string InvalidStreamKey => CurrentLanguage switch
        {
            "en" => "Invalid stream key",
            _ => "Geçersiz yayın anahtarı"
        };

        public static string EncoderNotFound => CurrentLanguage switch
        {
            "en" => "Encoder not found",
            _ => "Encoder bulunamadı"
        };

        public static string FFmpegNotFound => CurrentLanguage switch
        {
            "en" => "FFmpeg not found. Please install FFmpeg.",
            _ => "FFmpeg bulunamadı. Lütfen FFmpeg'i yükleyin."
        };

        #endregion

        #region License Errors

        public static string LicenseInvalid => CurrentLanguage switch
        {
            "en" => "Invalid license",
            _ => "Geçersiz lisans"
        };

        public static string LicenseExpired => CurrentLanguage switch
        {
            "en" => "License expired",
            _ => "Lisans süresi dolmuş"
        };

        public static string LicenseActivationFailed => CurrentLanguage switch
        {
            "en" => "License activation failed",
            _ => "Lisans aktivasyonu başarısız"
        };

        public static string TrialExpired => CurrentLanguage switch
        {
            "en" => "Trial period has expired",
            _ => "Deneme süresi dolmuş"
        };

        public static string HardwareMismatch => CurrentLanguage switch
        {
            "en" => "Hardware ID mismatch. License may have been transferred.",
            _ => "Donanım kimliği uyuşmuyor. Lisans başka bir cihaza taşınmış olabilir."
        };

        #endregion

        #region Platform Specific

        public static string YouTubeQuotaExceeded => CurrentLanguage switch
        {
            "en" => "YouTube API quota exceeded. Please try again tomorrow.",
            _ => "YouTube API kotası aşıldı. Lütfen yarın tekrar deneyin."
        };

        public static string TwitchAuthExpired => CurrentLanguage switch
        {
            "en" => "Twitch authentication expired. Please re-authenticate.",
            _ => "Twitch kimlik doğrulaması süresi doldu. Lütfen tekrar giriş yapın."
        };

        public static string InstagramRateLimited => CurrentLanguage switch
        {
            "en" => "Instagram rate limit reached. Please wait before trying again.",
            _ => "Instagram istek limiti aşıldı. Lütfen bir süre bekleyin."
        };

        public static string TikTokSignServerUnavailable => CurrentLanguage switch
        {
            "en" => "TikTok sign server unavailable. Chat may not work.",
            _ => "TikTok imza sunucusu erişilemiyor. Chat çalışmayabilir."
        };

        public static string FacebookTokenExpired => CurrentLanguage switch
        {
            "en" => "Facebook access token expired. Please re-authenticate.",
            _ => "Facebook erişim anahtarı süresi doldu. Lütfen tekrar giriş yapın."
        };

        #endregion

        #region Settings Errors

        public static string SettingsSaveFailed => CurrentLanguage switch
        {
            "en" => "Failed to save settings",
            _ => "Ayarlar kaydedilemedi"
        };

        public static string SettingsLoadFailed => CurrentLanguage switch
        {
            "en" => "Failed to load settings",
            _ => "Ayarlar yüklenemedi"
        };

        public static string SettingsSaved => CurrentLanguage switch
        {
            "en" => "Settings saved successfully",
            _ => "Ayarlar başarıyla kaydedildi"
        };

        public static string InvalidVideoResolution => CurrentLanguage switch
        {
            "en" => "Invalid video resolution",
            _ => "Geçersiz video çözünürlüğü"
        };

        public static string InvalidBitrate => CurrentLanguage switch
        {
            "en" => "Invalid bitrate value",
            _ => "Geçersiz bitrate değeri"
        };

        #endregion

        #region Device Errors

        public static string CameraNotFound => CurrentLanguage switch
        {
            "en" => "Camera not found",
            _ => "Kamera bulunamadı"
        };

        public static string MicrophoneNotFound => CurrentLanguage switch
        {
            "en" => "Microphone not found",
            _ => "Mikrofon bulunamadı"
        };

        public static string DeviceInUse => CurrentLanguage switch
        {
            "en" => "Device is in use by another application",
            _ => "Cihaz başka bir uygulama tarafından kullanılıyor"
        };

        public static string DeviceAccessDenied => CurrentLanguage switch
        {
            "en" => "Device access denied. Please check permissions.",
            _ => "Cihaz erişimi reddedildi. Lütfen izinleri kontrol edin."
        };

        #endregion

        #region General Errors

        public static string UnexpectedError => CurrentLanguage switch
        {
            "en" => "An unexpected error occurred",
            _ => "Beklenmeyen bir hata oluştu"
        };

        public static string NetworkError => CurrentLanguage switch
        {
            "en" => "Network error. Please check your connection.",
            _ => "Ağ hatası. Lütfen bağlantınızı kontrol edin."
        };

        public static string OperationCancelled => CurrentLanguage switch
        {
            "en" => "Operation cancelled",
            _ => "İşlem iptal edildi"
        };

        public static string PermissionDenied => CurrentLanguage switch
        {
            "en" => "Permission denied",
            _ => "İzin reddedildi"
        };

        public static string FileNotFound => CurrentLanguage switch
        {
            "en" => "File not found",
            _ => "Dosya bulunamadı"
        };

        public static string DiskFull => CurrentLanguage switch
        {
            "en" => "Disk is full",
            _ => "Disk dolu"
        };

        #endregion

        #region UI Labels

        public static string Ready => CurrentLanguage switch
        {
            "en" => "Ready",
            _ => "Hazır"
        };

        public static string Live => CurrentLanguage switch
        {
            "en" => "LIVE",
            _ => "CANLI"
        };

        public static string MessagesPerMinute => CurrentLanguage switch
        {
            "en" => "messages/min",
            _ => "mesaj/dk"
        };

        public static string Platforms => CurrentLanguage switch
        {
            "en" => "Platforms:",
            _ => "Platformlar:"
        };

        #endregion

        #region Helper Methods

        /// <summary>
        /// Parametreli hata mesajı formatla
        /// </summary>
        public static string Format(string template, params object[] args)
        {
            try
            {
                return string.Format(template, args);
            }
            catch
            {
                return template;
            }
        }

        /// <summary>
        /// Platform bazlı bağlantı durumu mesajı
        /// </summary>
        public static string GetPlatformStatusMessage(string platform, string status)
        {
            return CurrentLanguage switch
            {
                "en" => $"{platform}: {status}",
                _ => $"{platform}: {status}"
            };
        }

        /// <summary>
        /// Retry mesajı
        /// </summary>
        public static string GetRetryMessage(int attempt, int maxAttempts)
        {
            return CurrentLanguage switch
            {
                "en" => $"Retry attempt {attempt} of {maxAttempts}",
                _ => $"Yeniden deneme {attempt}/{maxAttempts}"
            };
        }

        #endregion
    }
}
