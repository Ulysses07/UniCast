using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UniCast.Licensing.Models
{
    /// <summary>
    /// Lisans türleri.
    /// </summary>
    public enum LicenseType
    {
        /// <summary>14 günlük deneme - watermark ile</summary>
        Trial = 0,

        /// <summary>Tek makine, temel özellikler</summary>
        Personal = 1,

        /// <summary>3 makine, tüm özellikler</summary>
        Professional = 2,

        /// <summary>Sınırsız makine, tüm özellikler + öncelikli destek</summary>
        Enterprise = 3
    }

    /// <summary>
    /// Lisans durumu.
    /// </summary>
    public enum LicenseStatus
    {
        NotFound = 0,
        Valid = 1,
        Expired = 2,
        HardwareMismatch = 3,
        InvalidSignature = 4,
        Revoked = 5,
        MachineLimitExceeded = 6,
        GracePeriod = 7,
        Tampered = 8,
        ServerUnreachable = 9
    }

    /// <summary>
    /// Özellik bayrakları (bit flags).
    /// </summary>
    [Flags]
    public enum LicenseFeatures : long
    {
        None = 0,

        // Temel özellikler (Trial)
        SingleStream = 1 << 0,
        BasicOverlay = 1 << 1,
        LocalRecording = 1 << 2,

        // Personal özellikler
        MultiStream = 1 << 3,
        ChatOverlay = 1 << 4,
        CustomOverlay = 1 << 5,

        // Professional özellikler
        ScreenCapture = 1 << 6,
        MultipleScenes = 1 << 7,
        ScheduledStreams = 1 << 8,
        Analytics = 1 << 9,
        NoWatermark = 1 << 10,

        // Enterprise özellikler
        ApiAccess = 1 << 11,
        PrioritySupport = 1 << 12,
        WhiteLabel = 1 << 13,
        CustomBranding = 1 << 14,
        UnlimitedMachines = 1 << 15,

        // Paket tanımları
        TrialFeatures = SingleStream | BasicOverlay | LocalRecording,
        PersonalFeatures = TrialFeatures | MultiStream | ChatOverlay | CustomOverlay | NoWatermark,
        ProfessionalFeatures = PersonalFeatures | ScreenCapture | MultipleScenes | ScheduledStreams | Analytics,
        EnterpriseFeatures = ProfessionalFeatures | ApiAccess | PrioritySupport | WhiteLabel | CustomBranding | UnlimitedMachines
    }

    /// <summary>
    /// Ana lisans verisi - şifrelenmiş olarak saklanır.
    /// </summary>
    public sealed class LicenseData
    {
        /// <summary>Benzersiz lisans ID (GUID)</summary>
        public string LicenseId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Lisans anahtarı (kullanıcının girdiği) - Format: XXXXX-XXXXX-XXXXX-XXXXX-XXXXX</summary>
        public string LicenseKey { get; set; } = "";

        /// <summary>Lisans türü</summary>
        public LicenseType Type { get; set; } = LicenseType.Trial;

        /// <summary>Aktif özellikler</summary>
        public LicenseFeatures Features { get; set; } = LicenseFeatures.TrialFeatures;

        /// <summary>Lisans sahibi adı</summary>
        public string LicenseeName { get; set; } = "";

        /// <summary>Lisans sahibi email</summary>
        public string LicenseeEmail { get; set; } = "";

        /// <summary>Şirket adı (opsiyonel)</summary>
        public string? CompanyName { get; set; }

        /// <summary>Oluşturulma tarihi (UTC)</summary>
        public DateTime IssuedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Geçerlilik bitiş tarihi (UTC)</summary>
        public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddDays(14);

        /// <summary>Son başarılı doğrulama (UTC)</summary>
        public DateTime LastValidationUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Aktivasyon yapılan Hardware ID'ler</summary>
        public List<HardwareActivation> Activations { get; set; } = [];

        /// <summary>Maksimum izin verilen makine sayısı</summary>
        public int MaxMachines { get; set; } = 1;

        /// <summary>Çevrimdışı tolerans süresi (gün)</summary>
        public int OfflineGraceDays { get; set; } = 7;

        /// <summary>Lisans şema versiyonu</summary>
        public int SchemaVersion { get; set; } = 1;

        /// <summary>RSA-2048 dijital imza (Base64)</summary>
        public string Signature { get; set; } = "";

        /// <summary>Sunucu tarafı checksum (kurcalama tespiti)</summary>
        public string ServerChecksum { get; set; } = "";

        // Computed properties
        [JsonIgnore] public bool IsExpired => DateTime.UtcNow > ExpiresAtUtc;
        [JsonIgnore] public int DaysRemaining => Math.Max(0, (ExpiresAtUtc - DateTime.UtcNow).Days);
        [JsonIgnore] public bool IsTrial => Type == LicenseType.Trial;
        [JsonIgnore] public bool RequiresWatermark => !Features.HasFlag(LicenseFeatures.NoWatermark);

        public bool HasFeature(LicenseFeatures feature) => Features.HasFlag(feature);

        /// <summary>İmzalanacak veri (Signature ve ServerChecksum hariç tüm alanlar)</summary>
        public string GetSignableContent()
        {
            return string.Join("|",
                LicenseId,
                LicenseKey,
                (int)Type,
                (long)Features,
                LicenseeName,
                LicenseeEmail,
                CompanyName ?? "",
                IssuedAtUtc.ToString("O"),
                ExpiresAtUtc.ToString("O"),
                MaxMachines,
                OfflineGraceDays,
                SchemaVersion,
                string.Join(",", Activations.ConvertAll(a => a.HardwareId))
            );
        }
    }

    /// <summary>
    /// Makine aktivasyon kaydı.
    /// </summary>
    public sealed class HardwareActivation
    {
        public string HardwareId { get; set; } = "";
        public string HardwareIdShort { get; set; } = "";
        public string MachineName { get; set; } = "";
        public DateTime ActivatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
        public string ComponentsHash { get; set; } = "";
    }

    /// <summary>
    /// Lisans doğrulama sonucu.
    /// </summary>
    public sealed class LicenseValidationResult
    {
        public bool IsValid { get; set; }
        public LicenseStatus Status { get; set; }
        public LicenseData? License { get; set; }
        public string Message { get; set; } = "";
        public string? ErrorCode { get; set; }
        public int? GraceDaysRemaining { get; set; }

        public static LicenseValidationResult Success(LicenseData license) => new()
        {
            IsValid = true,
            Status = LicenseStatus.Valid,
            License = license,
            Message = "Lisans geçerli"
        };

        public static LicenseValidationResult Failure(LicenseStatus status, string message, string? errorCode = null) => new()
        {
            IsValid = false,
            Status = status,
            Message = message,
            ErrorCode = errorCode
        };

        public static LicenseValidationResult Grace(LicenseData license, int daysRemaining) => new()
        {
            IsValid = true,
            Status = LicenseStatus.GracePeriod,
            License = license,
            Message = $"Çevrimdışı mod - {daysRemaining} gün kaldı",
            GraceDaysRemaining = daysRemaining
        };
    }
}