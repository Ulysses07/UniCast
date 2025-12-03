using System;
using System.Collections.Generic;
using System.Globalization;

namespace UniCast.App.Exceptions
{
    /// <summary>
    /// DÜZELTME v20: Lokalize edilmiş Exception'lar
    /// Kullanıcı dostu hata mesajları
    /// </summary>
    public abstract class LocalizedException : Exception
    {
        /// <summary>Hata kodu</summary>
        public string ErrorCode { get; }

        /// <summary>Kullanıcı dostu mesaj</summary>
        public string UserMessage { get; }

        /// <summary>Teknik detaylar</summary>
        public Dictionary<string, object> Details { get; } = new();

        protected LocalizedException(string errorCode, string userMessage, string? technicalMessage = null, Exception? innerException = null)
            : base(technicalMessage ?? userMessage, innerException)
        {
            ErrorCode = errorCode;
            UserMessage = userMessage;
        }

        public override string ToString()
        {
            return $"[{ErrorCode}] {UserMessage}\n{base.ToString()}";
        }

        /// <summary>Mevcut dil (tr veya en)</summary>
        protected static string Lang => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";
    }

    #region Connection Exceptions

    /// <summary>
    /// Bağlantı hataları
    /// </summary>
    public class ConnectionException : LocalizedException
    {
        public string? Platform { get; }

        public ConnectionException(string platform, Exception? inner = null)
            : base("CONN_001",
                   Lang == "tr" ? $"{platform} bağlantısı başarısız oldu." : $"Connection to {platform} failed.",
                   $"Connection failed for {platform}",
                   inner)
        {
            Platform = platform;
            Details["Platform"] = platform;
        }

        public static ConnectionException Timeout(string platform, TimeSpan timeout)
        {
            var ex = new ConnectionException(platform);
            ex.Details["Timeout"] = timeout.TotalSeconds;
            return ex;
        }

        public static ConnectionException NetworkUnavailable()
        {
            return new ConnectionException("Network")
            {
                Details = { ["Reason"] = "Network unavailable" }
            };
        }
    }

    /// <summary>
    /// Stream hataları
    /// </summary>
    public class StreamException : LocalizedException
    {
        public StreamErrorType ErrorType { get; }

        public StreamException(StreamErrorType errorType, string? details = null, Exception? inner = null)
            : base($"STREAM_{(int)errorType:D3}",
                   GetUserMessage(errorType),
                   details ?? GetUserMessage(errorType),
                   inner)
        {
            ErrorType = errorType;
            Details["ErrorType"] = errorType.ToString();
        }

        private static string GetUserMessage(StreamErrorType errorType)
        {
            var isTr = Lang == "tr";
            return errorType switch
            {
                StreamErrorType.EncoderNotFound => isTr ? "FFmpeg bulunamadı." : "FFmpeg not found.",
                StreamErrorType.InvalidSettings => isTr ? "Geçersiz stream ayarları." : "Invalid stream settings.",
                StreamErrorType.ConnectionLost => isTr ? "Stream bağlantısı kesildi." : "Stream connection lost.",
                StreamErrorType.BitrateError => isTr ? "Bitrate hatası." : "Bitrate error.",
                StreamErrorType.AudioDeviceError => isTr ? "Ses cihazı hatası." : "Audio device error.",
                StreamErrorType.VideoDeviceError => isTr ? "Video cihazı hatası." : "Video device error.",
                _ => isTr ? "Stream hatası oluştu." : "Stream error occurred."
            };
        }
    }

    public enum StreamErrorType
    {
        Unknown = 0,
        EncoderNotFound = 1,
        InvalidSettings = 2,
        ConnectionLost = 3,
        BitrateError = 4,
        AudioDeviceError = 5,
        VideoDeviceError = 6,
        StartFailed = 7,
        StopFailed = 8
    }

    #endregion

    #region License Exceptions

    /// <summary>
    /// Lisans hataları
    /// </summary>
    public class LicenseException : LocalizedException
    {
        public LicenseErrorType ErrorType { get; }

        public LicenseException(LicenseErrorType errorType, string? details = null, Exception? inner = null)
            : base($"LICENSE_{(int)errorType:D3}",
                   GetUserMessage(errorType),
                   details ?? GetUserMessage(errorType),
                   inner)
        {
            ErrorType = errorType;
            Details["ErrorType"] = errorType.ToString();
        }

        private static string GetUserMessage(LicenseErrorType errorType)
        {
            var isTr = Lang == "tr";
            return errorType switch
            {
                LicenseErrorType.Invalid => isTr ? "Geçersiz lisans anahtarı." : "Invalid license key.",
                LicenseErrorType.Expired => isTr ? "Lisansınız sona erdi." : "Your license has expired.",
                LicenseErrorType.ServerUnreachable => isTr ? "Lisans sunucusuna ulaşılamıyor." : "Cannot reach license server.",
                LicenseErrorType.HardwareMismatch => isTr ? "Donanım uyuşmazlığı." : "Hardware mismatch.",
                LicenseErrorType.RateLimited => isTr ? "Çok fazla deneme." : "Too many attempts.",
                _ => isTr ? "Lisans doğrulama hatası." : "License validation error."
            };
        }
    }

    public enum LicenseErrorType
    {
        Unknown = 0,
        Invalid = 1,
        Expired = 2,
        ServerUnreachable = 3,
        HardwareMismatch = 4,
        RateLimited = 5,
        ActivationFailed = 6,
        DeactivationFailed = 7
    }

    #endregion

    #region Platform Exceptions

    /// <summary>
    /// Platform-specific hatalar
    /// </summary>
    public class PlatformException : LocalizedException
    {
        public string Platform { get; }
        public PlatformErrorType ErrorType { get; }

        public PlatformException(string platform, PlatformErrorType errorType, string? details = null, Exception? inner = null)
            : base($"PLAT_{platform.ToUpper()}_{(int)errorType:D3}",
                   GetUserMessage(platform, errorType),
                   details ?? GetUserMessage(platform, errorType),
                   inner)
        {
            Platform = platform;
            ErrorType = errorType;
            Details["Platform"] = platform;
            Details["ErrorType"] = errorType.ToString();
        }

        private static string GetUserMessage(string platform, PlatformErrorType errorType)
        {
            var isTr = Lang == "tr";
            return errorType switch
            {
                PlatformErrorType.AuthenticationFailed => isTr ? $"{platform} kimlik doğrulaması başarısız." : $"{platform} authentication failed.",
                PlatformErrorType.TokenExpired => isTr ? $"{platform} oturum süresi doldu." : $"{platform} session expired.",
                PlatformErrorType.RateLimited => isTr ? $"{platform} istek limiti aşıldı." : $"{platform} rate limit exceeded.",
                PlatformErrorType.ApiError => isTr ? $"{platform} API hatası." : $"{platform} API error.",
                PlatformErrorType.NotLive => isTr ? $"{platform} canlı yayında değil." : $"{platform} is not live.",
                _ => isTr ? $"{platform} platformunda hata oluştu." : $"Error occurred on {platform}."
            };
        }
    }

    public enum PlatformErrorType
    {
        Unknown = 0,
        AuthenticationFailed = 1,
        TokenExpired = 2,
        RateLimited = 3,
        ApiError = 4,
        NotLive = 5,
        ChatDisabled = 6,
        PermissionDenied = 7
    }

    #endregion

    #region Settings Exceptions

    /// <summary>
    /// Ayar hataları
    /// </summary>
    public class SettingsException : LocalizedException
    {
        public string SettingName { get; }

        public SettingsException(string settingName, string reason, Exception? inner = null)
            : base("SETTINGS_001",
                   Lang == "tr" ? $"'{settingName}' ayarı geçersiz." : $"'{settingName}' setting is invalid.",
                   $"Setting '{settingName}' is invalid: {reason}",
                   inner)
        {
            SettingName = settingName;
            Details["SettingName"] = settingName;
            Details["Reason"] = reason;
        }

        public static SettingsException OutOfRange(string settingName, object value, object min, object max)
        {
            return new SettingsException(settingName, $"Value {value} is out of range [{min}, {max}]")
            {
                Details =
                {
                    ["Value"] = value,
                    ["Min"] = min,
                    ["Max"] = max
                }
            };
        }

        public static SettingsException Required(string settingName)
        {
            return new SettingsException(settingName, "Value is required");
        }

        public static SettingsException InvalidFormat(string settingName, string expectedFormat)
        {
            return new SettingsException(settingName, $"Expected format: {expectedFormat}")
            {
                Details = { ["ExpectedFormat"] = expectedFormat }
            };
        }
    }

    #endregion

    #region Device Exceptions

    /// <summary>
    /// Cihaz hataları
    /// </summary>
    public class DeviceException : LocalizedException
    {
        public string DeviceName { get; }
        public DeviceType Type { get; }

        public DeviceException(DeviceType type, string deviceName, string reason, Exception? inner = null)
            : base($"DEVICE_{type.ToString().ToUpper()}_001",
                   GetUserMessage(type, deviceName),
                   $"Device error ({type}): {deviceName} - {reason}",
                   inner)
        {
            DeviceName = deviceName;
            Type = type;
            Details["DeviceName"] = deviceName;
            Details["DeviceType"] = type.ToString();
            Details["Reason"] = reason;
        }

        private static string GetUserMessage(DeviceType type, string deviceName)
        {
            var isTr = Lang == "tr";
            return type switch
            {
                DeviceType.Camera => isTr ? $"Kamera hatası: {deviceName}" : $"Camera error: {deviceName}",
                DeviceType.Microphone => isTr ? $"Mikrofon hatası: {deviceName}" : $"Microphone error: {deviceName}",
                DeviceType.Speaker => isTr ? $"Hoparlör hatası: {deviceName}" : $"Speaker error: {deviceName}",
                DeviceType.Screen => isTr ? $"Ekran yakalama hatası: {deviceName}" : $"Screen capture error: {deviceName}",
                _ => isTr ? $"Cihaz hatası: {deviceName}" : $"Device error: {deviceName}"
            };
        }
    }

    public enum DeviceType
    {
        Unknown,
        Camera,
        Microphone,
        Speaker,
        Screen
    }

    #endregion

    #region Exception Extensions

    public static class ExceptionExtensions
    {
        /// <summary>
        /// Exception'dan kullanıcı dostu mesaj al
        /// </summary>
        public static string GetUserFriendlyMessage(this Exception ex)
        {
            if (ex is LocalizedException localizedEx)
            {
                return localizedEx.UserMessage;
            }

            var isTr = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";

            return ex switch
            {
                TimeoutException => isTr ? "İşlem zaman aşımına uğradı." : "Operation timed out.",
                OperationCanceledException => isTr ? "İşlem iptal edildi." : "Operation was cancelled.",
                UnauthorizedAccessException => isTr ? "Erişim reddedildi." : "Access denied.",
                System.IO.IOException => isTr ? "Dosya işlemi başarısız." : "File operation failed.",
                System.Net.Http.HttpRequestException => isTr ? "Ağ bağlantısı hatası." : "Network connection error.",
                _ => isTr ? "Beklenmeyen bir hata oluştu." : "An unexpected error occurred."
            };
        }

        /// <summary>
        /// Exception'ı logla ve kullanıcı dostu mesaj döndür
        /// </summary>
        public static string LogAndGetMessage(this Exception ex, Serilog.ILogger logger, string context)
        {
            if (ex is LocalizedException localizedEx)
            {
                logger.Error(ex, "[{Context}] {ErrorCode}: {Message}", context, localizedEx.ErrorCode, localizedEx.UserMessage);
                return localizedEx.UserMessage;
            }

            logger.Error(ex, "[{Context}] {Message}", context, ex.Message);
            return ex.GetUserFriendlyMessage();
        }
    }

    #endregion
}