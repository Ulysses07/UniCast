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

        /// <summary>Lisans sahibi adı</summary>
        public string LicenseeName { get; set; } = "";

        /// <summary>Lisans sahibi e-posta</summary>
        public string LicenseeEmail { get; set; } = "";

        /// <summary>Lisans verilme tarihi (UTC)</summary>
        public DateTime IssuedAtUtc { get; set; }

        /// <summary>Lisans bitiş tarihi (UTC) - Trial için 14 gün, Lifetime için DateTime.MaxValue</summary>
        public DateTime ExpiresAtUtc { get; set; }

        /// <summary>Bakım/Destek bitiş tarihi (UTC) - Yıllık yenilenir</summary>
        public DateTime SupportExpiryUtc { get; set; }

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

        /// <summary>Lifetime lisansı mı?</summary>
        [JsonIgnore]
        public bool IsLifetime => Type == LicenseType.Lifetime;

        /// <summary>Lisans süresi dolmuş mu? (Trial için geçerli, Lifetime asla dolmaz)</summary>
        [JsonIgnore]
        public bool IsExpired => DateTime.UtcNow > ExpiresAtUtc;

        /// <summary>Kalan gün sayısı (Trial için)</summary>
        [JsonIgnore]
        public int DaysRemaining
        {
            get
            {
                if (IsLifetime) return int.MaxValue;
                var remaining = (ExpiresAtUtc - DateTime.UtcNow).TotalDays;
                return remaining > 0 ? (int)Math.Ceiling(remaining) : 0;
            }
        }

        /// <summary>Bakım/Destek aktif mi?</summary>
        [JsonIgnore]
        public bool IsSupportActive => DateTime.UtcNow <= SupportExpiryUtc;

        /// <summary>Bakım/Destek için kalan gün sayısı</summary>
        [JsonIgnore]
        public int SupportDaysRemaining
        {
            get
            {
                var remaining = (SupportExpiryUtc - DateTime.UtcNow).TotalDays;
                return remaining > 0 ? (int)Math.Ceiling(remaining) : 0;
            }
        }

        #endregion

        #region Methods

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
            sb.Append(LicenseeName);
            sb.Append('|');
            sb.Append(LicenseeEmail);
            sb.Append('|');
            sb.Append(IssuedAtUtc.ToString("O"));
            sb.Append('|');
            sb.Append(ExpiresAtUtc.ToString("O"));
            sb.Append('|');
            sb.Append(SupportExpiryUtc.ToString("O"));
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
            var licenseInfo = IsLifetime ? "Ömür Boyu" : $"Trial ({DaysRemaining} gün)";
            var supportInfo = IsSupportActive ? $"Destek: {SupportDaysRemaining} gün" : "Destek: Süresi doldu";
            return $"License[{licenseInfo}] {LicenseId[..8]}... - {LicenseeName} - {supportInfo}";
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

        /// <summary>Ömür boyu lisans (yazılım sonsuza kadar çalışır)</summary>
        Lifetime = 1
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

        /// <summary>Süresi dolmuş (Trial için)</summary>
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

        /// <summary>Destek/bakım süresi dolmuş (yazılım çalışır ama güncelleme/destek yok)</summary>
        SupportExpired = 10,

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

        /// <summary>
        /// Destek süresi dolmuş ama yazılım çalışır durumda.
        /// </summary>
        public static LicenseValidationResult SupportExpired(LicenseData license)
        {
            return new LicenseValidationResult
            {
                IsValid = true, // Yazılım hala çalışır!
                Status = LicenseStatus.SupportExpired,
                Message = "Bakım/destek süreniz doldu. Yazılım çalışmaya devam edecek ancak güncelleme ve destek alamazsınız.",
                License = license
            };
        }

        public override string ToString()
        {
            return $"[{Status}] {(IsValid ? "✓" : "✗")} {Message}";
        }
    }
}