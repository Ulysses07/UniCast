using System;
using System.Collections.Generic;
using System.Globalization;

namespace UniCast.App.Resources
{
    /// <summary>
    /// DÜZELTME v20: Ek Hata Mesajları
    /// ErrorStrings sınıfını genişleten ek mesajlar
    /// </summary>
    public static class ErrorStringsEx
    {
        #region Additional Error Messages

        private static readonly Dictionary<string, Dictionary<string, string>> _additionalMessages = new()
        {
            // Genel Hatalar
            ["General.Timeout"] = new()
            {
                ["tr"] = "İşlem zaman aşımına uğradı. Lütfen tekrar deneyin.",
                ["en"] = "Operation timed out. Please try again."
            },
            ["General.Cancelled"] = new()
            {
                ["tr"] = "İşlem iptal edildi.",
                ["en"] = "Operation was cancelled."
            },
            ["General.AccessDenied"] = new()
            {
                ["tr"] = "Erişim reddedildi. Yetkiniz yok.",
                ["en"] = "Access denied. You don't have permission."
            },
            ["General.IOError"] = new()
            {
                ["tr"] = "Dosya işlemi başarısız oldu.",
                ["en"] = "File operation failed."
            },
            ["General.NetworkError"] = new()
            {
                ["tr"] = "Ağ bağlantısı hatası. İnternet bağlantınızı kontrol edin.",
                ["en"] = "Network connection error. Check your internet connection."
            },
            ["General.UnexpectedError"] = new()
            {
                ["tr"] = "Beklenmeyen bir hata oluştu.",
                ["en"] = "An unexpected error occurred."
            },

            // Bağlantı Hataları
            ["Connection.Failed"] = new()
            {
                ["tr"] = "{0} bağlantısı başarısız oldu.",
                ["en"] = "Connection to {0} failed."
            },
            ["Connection.Timeout"] = new()
            {
                ["tr"] = "{0} bağlantısı zaman aşımına uğradı.",
                ["en"] = "Connection to {0} timed out."
            },

            // Stream Hataları
            ["Stream.EncoderNotFound"] = new()
            {
                ["tr"] = "FFmpeg bulunamadı. Lütfen kurulumu kontrol edin.",
                ["en"] = "FFmpeg not found. Please check installation."
            },
            ["Stream.InvalidSettings"] = new()
            {
                ["tr"] = "Geçersiz stream ayarları.",
                ["en"] = "Invalid stream settings."
            },
            ["Stream.ConnectionLost"] = new()
            {
                ["tr"] = "Stream bağlantısı kesildi.",
                ["en"] = "Stream connection lost."
            },
            ["Stream.BitrateError"] = new()
            {
                ["tr"] = "Bitrate hatası. İnternet hızınız yeterli olmayabilir.",
                ["en"] = "Bitrate error. Your internet speed may be insufficient."
            },
            ["Stream.AudioDeviceError"] = new()
            {
                ["tr"] = "Ses cihazı hatası.",
                ["en"] = "Audio device error."
            },
            ["Stream.VideoDeviceError"] = new()
            {
                ["tr"] = "Video cihazı hatası.",
                ["en"] = "Video device error."
            },
            ["Stream.GenericError"] = new()
            {
                ["tr"] = "Stream hatası oluştu.",
                ["en"] = "Stream error occurred."
            },

            // Lisans Hataları
            ["License.Invalid"] = new()
            {
                ["tr"] = "Geçersiz lisans anahtarı.",
                ["en"] = "Invalid license key."
            },
            ["License.Expired"] = new()
            {
                ["tr"] = "Lisansınız sona erdi.",
                ["en"] = "Your license has expired."
            },
            ["License.ServerUnreachable"] = new()
            {
                ["tr"] = "Lisans sunucusuna ulaşılamıyor.",
                ["en"] = "Cannot reach license server."
            },
            ["License.HardwareMismatch"] = new()
            {
                ["tr"] = "Donanım uyuşmazlığı. Lisans bu cihaz için geçerli değil.",
                ["en"] = "Hardware mismatch. License is not valid for this device."
            },
            ["License.RateLimited"] = new()
            {
                ["tr"] = "Çok fazla deneme. Lütfen daha sonra tekrar deneyin.",
                ["en"] = "Too many attempts. Please try again later."
            },
            ["License.GenericError"] = new()
            {
                ["tr"] = "Lisans doğrulama hatası.",
                ["en"] = "License validation error."
            },

            // Platform Hataları
            ["Platform.AuthFailed"] = new()
            {
                ["tr"] = "{0} kimlik doğrulaması başarısız.",
                ["en"] = "{0} authentication failed."
            },
            ["Platform.TokenExpired"] = new()
            {
                ["tr"] = "{0} oturum süresi doldu. Yeniden giriş yapın.",
                ["en"] = "{0} session expired. Please login again."
            },
            ["Platform.RateLimited"] = new()
            {
                ["tr"] = "{0} istek limiti aşıldı. Bir süre bekleyin.",
                ["en"] = "{0} rate limit exceeded. Please wait."
            },
            ["Platform.ApiError"] = new()
            {
                ["tr"] = "{0} API hatası.",
                ["en"] = "{0} API error."
            },
            ["Platform.NotLive"] = new()
            {
                ["tr"] = "{0} canlı yayında değil.",
                ["en"] = "{0} is not live."
            },
            ["Platform.GenericError"] = new()
            {
                ["tr"] = "{0} platformunda hata oluştu.",
                ["en"] = "Error occurred on {0} platform."
            },

            // Ayar Hataları
            ["Settings.ValidationFailed"] = new()
            {
                ["tr"] = "'{0}' ayarı geçersiz.",
                ["en"] = "'{0}' setting is invalid."
            },
            ["Settings.SaveFailed"] = new()
            {
                ["tr"] = "Ayarlar kaydedilemedi.",
                ["en"] = "Failed to save settings."
            },
            ["Settings.LoadFailed"] = new()
            {
                ["tr"] = "Ayarlar yüklenemedi. Varsayılan ayarlar kullanılacak.",
                ["en"] = "Failed to load settings. Default settings will be used."
            },

            // Cihaz Hataları
            ["Device.CameraError"] = new()
            {
                ["tr"] = "Kamera hatası: {0}",
                ["en"] = "Camera error: {0}"
            },
            ["Device.MicrophoneError"] = new()
            {
                ["tr"] = "Mikrofon hatası: {0}",
                ["en"] = "Microphone error: {0}"
            },
            ["Device.SpeakerError"] = new()
            {
                ["tr"] = "Hoparlör hatası: {0}",
                ["en"] = "Speaker error: {0}"
            },
            ["Device.ScreenError"] = new()
            {
                ["tr"] = "Ekran yakalama hatası: {0}",
                ["en"] = "Screen capture error: {0}"
            },
            ["Device.GenericError"] = new()
            {
                ["tr"] = "Cihaz hatası: {0}",
                ["en"] = "Device error: {0}"
            },

            // Güncelleme Mesajları
            ["Update.Available"] = new()
            {
                ["tr"] = "Yeni sürüm mevcut: {0}",
                ["en"] = "New version available: {0}"
            },
            ["Update.Downloading"] = new()
            {
                ["tr"] = "Güncelleme indiriliyor... %{0}",
                ["en"] = "Downloading update... {0}%"
            },
            ["Update.Ready"] = new()
            {
                ["tr"] = "Güncelleme hazır. Yeniden başlatmak ister misiniz?",
                ["en"] = "Update ready. Would you like to restart?"
            },
            ["Update.Failed"] = new()
            {
                ["tr"] = "Güncelleme başarısız oldu.",
                ["en"] = "Update failed."
            }
        };

        #endregion

        #region Get Methods

        /// <summary>
        /// Hata mesajını al (format destekli) - Extension versiyonu
        /// </summary>
        /// <param name="key">Mesaj anahtarı</param>
        /// <param name="args">Format argümanları</param>
        /// <returns>Lokalize edilmiş mesaj</returns>
        public static string Get(string key, params object[] args)
        {
            var message = GetRawFromAdditional(key);

            if (args.Length > 0)
            {
                try
                {
                    return string.Format(message, args);
                }
                catch
                {
                    return message;
                }
            }

            return message;
        }

        /// <summary>
        /// Ham mesajı al (format uygulamadan)
        /// </summary>
        private static string GetRawFromAdditional(string key)
        {
            if (_additionalMessages.TryGetValue(key, out var translations))
            {
                var lang = GetCurrentLang();

                if (translations.TryGetValue(lang, out var message))
                    return message;

                // Fallback to English
                if (translations.TryGetValue("en", out message))
                    return message;

                // Fallback to any available
                return translations.Values.FirstOrDefault() ?? key;
            }

            return key;
        }

        /// <summary>
        /// Mevcut dili al (mevcut ErrorStrings.CurrentLanguage varsa onu kullan)
        /// </summary>
        private static string GetCurrentLang()
        {
            var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return culture == "tr" ? "tr" : "en";
        }

        /// <summary>
        /// Tüm desteklenen dilleri al
        /// </summary>
        public static IEnumerable<(string Code, string Name)> GetSupportedLanguages()
        {
            yield return ("tr", "Türkçe");
            yield return ("en", "English");
        }

        #endregion

        #region Validation Messages

        /// <summary>
        /// Validation hatası mesajı oluştur
        /// </summary>
        public static string ValidationError(string field, string reason)
        {
            return GetCurrentLang() == "tr"
                ? $"'{field}' alanı geçersiz: {reason}"
                : $"'{field}' field is invalid: {reason}";
        }

        /// <summary>
        /// Aralık dışı hatası
        /// </summary>
        public static string OutOfRange(string field, object min, object max)
        {
            return GetCurrentLang() == "tr"
                ? $"'{field}' {min} ile {max} arasında olmalıdır."
                : $"'{field}' must be between {min} and {max}.";
        }

        /// <summary>
        /// Zorunlu alan hatası
        /// </summary>
        public static string Required(string field)
        {
            return GetCurrentLang() == "tr"
                ? $"'{field}' zorunlu bir alandır."
                : $"'{field}' is required.";
        }

        #endregion
    }
}