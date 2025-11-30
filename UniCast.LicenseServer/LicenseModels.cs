using System;

namespace UniCast.Licensing
{
    /// <summary>
    /// Lisans türleri
    /// </summary>
    public enum LicenseType
    {
        Trial = 0,
        Personal = 1,
        Professional = 2,
        Business = 3,
        Enterprise = 4,
        MonthlySubscription = 10,
        YearlySubscription = 11,
        Lifetime = 20,
        Educational = 30,
        NFR = 99  // Not For Resale
    }

    /// <summary>
    /// Lisans özellikleri (bit flags)
    /// </summary>
    [Flags]
    public enum LicenseFeatures : long
    {
        None = 0,

        // Temel özellikler
        BasicStreaming = 1L << 0,
        MultiPlatform = 1L << 1,
        ChatIntegration = 1L << 2,
        Overlay = 1L << 3,
        Recording = 1L << 4,
        NoWatermark = 1L << 5,  // Filigran olmadan yayın

        // Profesyonel özellikler
        CustomBranding = 1L << 10,
        AdvancedAnalytics = 1L << 11,
        MultiCam = 1L << 12,
        VirtualCamera = 1L << 13,

        // İleri düzey özellikler
        RTMP = 1L << 20,
        SRT = 1L << 21,
        CloudStorage = 1L << 22,
        TeamCollaboration = 1L << 23,
        APIAccess = 1L << 24,
        WhiteLabel = 1L << 25,
        PrioritySupport = 1L << 26,

        // Kombinasyonlar
        TrialFeatures = BasicStreaming | ChatIntegration,
        StandardFeatures = BasicStreaming | MultiPlatform | ChatIntegration | Overlay | NoWatermark,
        ProFeatures = StandardFeatures | Recording | CustomBranding | AdvancedAnalytics | MultiCam | VirtualCamera,
        AllFeatures = ~0L
    }

    /// <summary>
    /// Lisans bilgisi modeli
    /// </summary>
    public class LicenseInfo
    {
        public string LicenseId { get; set; } = string.Empty;
        public string LicenseKey { get; set; } = string.Empty;
        public LicenseType Type { get; set; } = LicenseType.Trial;
        public LicenseFeatures Features { get; set; } = LicenseFeatures.TrialFeatures;
        public string LicenseeName { get; set; } = string.Empty;
        public string LicenseeEmail { get; set; } = string.Empty;
        public DateTime IssuedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public int MaxMachines { get; set; } = 1;
        public string HardwareId { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
        public bool IsValid { get; set; }
        public string? ValidationError { get; set; }

        /// <summary>
        /// Lisansın süresi dolmuş mu?
        /// </summary>
        public bool IsExpired => DateTime.UtcNow > ExpiresAt;

        /// <summary>
        /// Kalan gün sayısı
        /// </summary>
        public int DaysRemaining => Math.Max(0, (ExpiresAt - DateTime.UtcNow).Days);

        /// <summary>
        /// Belirli bir özellik aktif mi?
        /// </summary>
        public bool HasFeature(LicenseFeatures feature)
        {
            return (Features & feature) == feature;
        }

        /// <summary>
        /// Lisans türünün görünen adı
        /// </summary>
        public string TypeDisplayName => Type switch
        {
            LicenseType.Trial => "Deneme",
            LicenseType.Personal => "Kişisel",
            LicenseType.Professional => "Profesyonel",
            LicenseType.Business => "İşletme",
            LicenseType.Enterprise => "Kurumsal",
            LicenseType.MonthlySubscription => "Aylık Abonelik",
            LicenseType.YearlySubscription => "Yıllık Abonelik",
            LicenseType.Lifetime => "Ömür Boyu",
            LicenseType.Educational => "Eğitim",
            LicenseType.NFR => "NFR",
            _ => "Bilinmiyor"
        };
    }

    /// <summary>
    /// Aktivasyon sonucu
    /// </summary>
    public class ActivationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public LicenseInfo? License { get; set; }

        public static ActivationResult Succeeded(LicenseInfo license) => new()
        {
            Success = true,
            License = license
        };

        public static ActivationResult Failed(string error) => new()
        {
            Success = false,
            ErrorMessage = error
        };
    }

    /// <summary>
    /// Doğrulama sonucu
    /// </summary>
    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public string? ErrorMessage { get; set; }
        public LicenseInfo? License { get; set; }
        public ValidationFailureReason FailureReason { get; set; }

        public static ValidationResult Valid(LicenseInfo license) => new()
        {
            IsValid = true,
            License = license
        };

        public static ValidationResult Invalid(string error, ValidationFailureReason reason = ValidationFailureReason.Unknown) => new()
        {
            IsValid = false,
            ErrorMessage = error,
            FailureReason = reason
        };
    }

    /// <summary>
    /// Doğrulama başarısızlık nedenleri
    /// </summary>
    public enum ValidationFailureReason
    {
        Unknown = 0,
        NotFound = 1,
        Expired = 2,
        HardwareMismatch = 3,
        SignatureInvalid = 4,
        Revoked = 5,
        TamperDetected = 6,
        NetworkError = 7,
        ServerError = 8
    }

    /// <summary>
    /// Şifrelenmiş lisans verisi (yerel depolama için)
    /// </summary>
    public class EncryptedLicenseData
    {
        public string EncryptedPayload { get; set; } = string.Empty;
        public string IV { get; set; } = string.Empty;
        public string HardwareHash { get; set; } = string.Empty;
        public DateTime SavedAt { get; set; }
        public int Version { get; set; } = 1;
    }
}