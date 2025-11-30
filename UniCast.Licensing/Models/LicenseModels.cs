using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace UniCast.Licensing.Models
{
    /// <summary>
    /// Lisans veri modeli.
    /// Tüm lisans bilgilerini içerir.
    /// </summary>
    public sealed class LicenseData
    {
        /// <summary>Benzersiz lisans ID (GUID)</summary>
        public string LicenseId { get; set; } = "";

        /// <summary>Lisans anahtarı (XXXXX-XXXXX-XXXXX-XXXXX-XXXXX)</summary>
        public string LicenseKey { get; set; } = "";

        /// <summary>Lisans türü</summary>
        public LicenseType Type { get; set; } = LicenseType.Trial;

        /// <summary>Aktif özellikler</summary>
        public LicenseFeatures Features { get; set; } = LicenseFeatures.None;

        /// <summary>Lisans sahibi adı</summary>
        public string LicenseeName { get; set; } = "";

        /// <summary>Lisans sahibi e-posta</summary>
        public string LicenseeEmail { get; set; } = "";

        /// <summary>Lisans verilme tarihi (UTC)</summary>
        public DateTime IssuedAtUtc { get; set; }

        /// <summary>Lisans bitiş tarihi (UTC)</summary>
        public DateTime ExpiresAtUtc { get; set; }

        /// <summary>Maksimum makine sayısı</summary>
        public int MaxMachines { get; set; } = 1;

        /// <summary>Aktif makine aktivasyonları</summary>
        public List<HardwareActivation> Activations { get; set; } = new();

        /// <summary>Çevrimdışı grace period (gün)</summary>
        public int OfflineGraceDays { get; set; } = 7;

        /// <summary>Son online doğrulama zamanı (UTC)</summary>
        public DateTime LastValidationUtc { get; set; }

        /// <summary>RSA dijital imza</summary>
        public string Signature { get; set; } = "";

        /// <summary>Ek metadata</summary>
        public Dictionary<string, string> Metadata { get; set; } = new();

        #region Computed Properties

        /// <summary>Trial lisansı mı?</summary>
        [JsonIgnore]
        public bool IsTrial => Type == LicenseType.Trial;

        /// <summary>Süre dolmuş mu?</summary>
        [JsonIgnore]
        public bool IsExpired => DateTime.UtcNow > ExpiresAtUtc;

        /// <summary>Kalan gün sayısı</summary>
        [JsonIgnore]
        public int DaysRemaining
        {
            get
            {
                var remaining = (ExpiresAtUtc - DateTime.UtcNow).TotalDays;
                return remaining > 0 ? (int)Math.Ceiling(remaining) : 0;
            }
        }

        /// <summary>Subscription lisansı mı?</summary>
        [JsonIgnore]
        public bool IsSubscription => Type is LicenseType.MonthlySubscription or LicenseType.YearlySubscription;

        #endregion

        #region Methods

        /// <summary>
        /// Belirli bir özelliğin aktif olup olmadığını kontrol eder.
        /// </summary>
        public bool HasFeature(LicenseFeatures feature)
        {
            // Unlimited veya Enterprise her şeyi içerir
            if (Features.HasFlag(LicenseFeatures.AllFeatures))
                return true;

            return Features.HasFlag(feature);
        }

        /// <summary>
        /// İmzalanacak içeriği döndürür.
        /// İmza hesaplaması için kullanılır.
        /// </summary>
        public string GetSignableContent()
        {
            // İmza dışında tüm kritik alanları içer
            var sb = new StringBuilder();
            sb.Append(LicenseId);
            sb.Append('|');
            sb.Append(LicenseKey);
            sb.Append('|');
            sb.Append((int)Type);
            sb.Append('|');
            sb.Append((int)Features);
            sb.Append('|');
            sb.Append(LicenseeName);
            sb.Append('|');
            sb.Append(LicenseeEmail);
            sb.Append('|');
            sb.Append(IssuedAtUtc.ToString("O"));
            sb.Append('|');
            sb.Append(ExpiresAtUtc.ToString("O"));
            sb.Append('|');
            sb.Append(MaxMachines);

            // Aktivasyonları dahil et
            foreach (var activation in Activations)
            {
                sb.Append('|');
                sb.Append(activation.HardwareId);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Lisans bilgisi özeti.
        /// </summary>
        public override string ToString()
        {
            return $"License[{Type}] {LicenseId[..8]}... - {LicenseeName} - " +
                   $"Expires: {ExpiresAtUtc:yyyy-MM-dd} ({DaysRemaining} days)";
        }

        #endregion
    }

    /// <summary>
    /// Makine aktivasyon bilgisi.
    /// </summary>
    public sealed class HardwareActivation
    {
        /// <summary>Tam hardware ID (SHA256)</summary>
        public string HardwareId { get; set; } = "";

        /// <summary>Kısa hardware ID (ilk 16 karakter)</summary>
        public string HardwareIdShort { get; set; } = "";

        /// <summary>Makine adı</summary>
        public string MachineName { get; set; } = "";

        /// <summary>Aktivasyon tarihi (UTC)</summary>
        public DateTime ActivatedAtUtc { get; set; }

        /// <summary>Son görülme tarihi (UTC)</summary>
        public DateTime LastSeenUtc { get; set; }

        /// <summary>Bileşen hash'leri (benzerlik kontrolü için)</summary>
        public string ComponentsHash { get; set; } = "";

        /// <summary>IP adresi (opsiyonel)</summary>
        public string? IpAddress { get; set; }

        /// <summary>OS bilgisi</summary>
        public string? OsVersion { get; set; }
    }

    /// <summary>
    /// Lisans türleri.
    /// </summary>
    public enum LicenseType
    {
        /// <summary>Deneme sürümü (14 gün)</summary>
        Trial = 0,

        /// <summary>Kişisel lisans (1 makine)</summary>
        Personal = 1,

        /// <summary>Profesyonel lisans (3 makine)</summary>
        Professional = 2,

        /// <summary>İşletme lisansı (10 makine)</summary>
        Business = 3,

        /// <summary>Kurumsal lisans (sınırsız)</summary>
        Enterprise = 4,

        /// <summary>Aylık abonelik</summary>
        MonthlySubscription = 10,

        /// <summary>Yıllık abonelik</summary>
        YearlySubscription = 11,

        /// <summary>Ömür boyu lisans</summary>
        Lifetime = 20,

        /// <summary>Eğitim lisansı</summary>
        Educational = 30,

        /// <summary>NFR (Not For Resale)</summary>
        NFR = 40
    }

    /// <summary>
    /// Lisans özellikleri (bit flags).
    /// </summary>
    [Flags]
    public enum LicenseFeatures
    {
        None = 0,

        // Temel özellikler
        BasicStreaming = 1 << 0,        // Temel yayın
        MultiPlatform = 1 << 1,         // Çoklu platform
        ChatIntegration = 1 << 2,       // Chat entegrasyonu
        Overlay = 1 << 3,               // Overlay desteği
        Recording = 1 << 4,             // Kayıt özelliği

        // Gelişmiş özellikler
        CustomBranding = 1 << 5,        // Özel markalama
        AdvancedAnalytics = 1 << 6,     // Gelişmiş analitik
        MultiCam = 1 << 7,              // Çoklu kamera
        VirtualCamera = 1 << 8,         // Sanal kamera
        RTMP = 1 << 9,                  // RTMP desteği
        SRT = 1 << 10,                  // SRT desteği

        // Pro özellikler
        CloudStorage = 1 << 11,         // Bulut depolama
        TeamCollaboration = 1 << 12,    // Ekip işbirliği
        APIAccess = 1 << 13,            // API erişimi
        WhiteLabel = 1 << 14,           // Beyaz etiket
        PrioritySupport = 1 << 15,      // Öncelikli destek

        // Kombinasyonlar
        TrialFeatures = BasicStreaming | MultiPlatform | ChatIntegration | Overlay,

        StandardFeatures = TrialFeatures | Recording | CustomBranding,

        ProFeatures = StandardFeatures | AdvancedAnalytics | MultiCam |
                      VirtualCamera | RTMP | SRT,

        EnterpriseFeatures = ProFeatures | CloudStorage | TeamCollaboration |
                             APIAccess | WhiteLabel | PrioritySupport,

        AllFeatures = int.MaxValue
    }

    /// <summary>
    /// Lisans doğrulama durumu.
    /// </summary>
    public enum LicenseStatus
    {
        /// <summary>Geçerli lisans</summary>
        Valid = 0,

        /// <summary>Lisans bulunamadı</summary>
        NotFound = 1,

        /// <summary>Süresi dolmuş</summary>
        Expired = 2,

        /// <summary>Donanım uyuşmazlığı</summary>
        HardwareMismatch = 3,

        /// <summary>Geçersiz imza</summary>
        InvalidSignature = 4,

        /// <summary>İptal edilmiş</summary>
        Revoked = 5,

        /// <summary>Manipüle edilmiş</summary>
        Tampered = 6,

        /// <summary>Maksimum makine sayısı aşıldı</summary>
        MachineLimitExceeded = 7,

        /// <summary>Sunucuya ulaşılamıyor</summary>
        ServerUnreachable = 8,

        /// <summary>Çevrimdışı grace period</summary>
        GracePeriod = 9,

        /// <summary>Bilinmeyen hata</summary>
        Unknown = 99
    }

    /// <summary>
    /// Lisans doğrulama sonucu.
    /// </summary>
    public sealed class LicenseValidationResult
    {
        public bool IsValid { get; private init; }
        public LicenseStatus Status { get; private init; }
        public string Message { get; private init; } = "";
        public string? Details { get; private init; }
        public LicenseData? License { get; private init; }
        public int GraceDaysRemaining { get; private init; }

        private LicenseValidationResult() { }

        public static LicenseValidationResult Success(LicenseData license)
        {
            return new LicenseValidationResult
            {
                IsValid = true,
                Status = LicenseStatus.Valid,
                Message = "Lisans geçerli",
                License = license
            };
        }

        public static LicenseValidationResult Failure(LicenseStatus status, string message, string? details = null)
        {
            return new LicenseValidationResult
            {
                IsValid = false,
                Status = status,
                Message = message,
                Details = details
            };
        }

        public static LicenseValidationResult Grace(LicenseData license, int daysRemaining)
        {
            return new LicenseValidationResult
            {
                IsValid = true, // Grace period'da hala geçerli
                Status = LicenseStatus.GracePeriod,
                Message = $"Çevrimdışı mod - {daysRemaining} gün kaldı",
                License = license,
                GraceDaysRemaining = daysRemaining
            };
        }

        public override string ToString()
        {
            return $"[{Status}] {(IsValid ? "✓" : "✗")} {Message}";
        }
    }
}