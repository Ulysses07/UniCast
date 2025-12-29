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
        [JsonPropertyName("licenseId")]
        public string LicenseId { get; set; } = "";

        /// <summary>Lisans anahtarý (XXXXX-XXXXX-XXXXX-XXXXX-XXXXX)</summary>
        [JsonPropertyName("licenseKey")]
        public string LicenseKey { get; set; } = "";

        /// <summary>Lisans türü</summary>
        [JsonPropertyName("type")]
        public LicenseType Type { get; set; } = LicenseType.Trial;

        /// <summary>Lisans sahibi adý</summary>
        [JsonPropertyName("licenseeName")]
        public string LicenseeName { get; set; } = "";

        /// <summary>Lisans sahibi e-posta</summary>
        [JsonPropertyName("licenseeEmail")]
        public string LicenseeEmail { get; set; } = "";

        /// <summary>Lisans verilme tarihi (UTC)</summary>
        [JsonPropertyName("issuedAtUtc")]
        public DateTime IssuedAtUtc { get; set; }

        /// <summary>Lisans bitiþ tarihi (UTC) - Trial için 14 gün, Lifetime için DateTime.MaxValue</summary>
        [JsonPropertyName("expiresAtUtc")]
        public DateTime ExpiresAtUtc { get; set; }

        /// <summary>Bakým/Destek bitiþ tarihi (UTC) - Yýllýk yenilenir</summary>
        [JsonPropertyName("supportExpiryUtc")]
        public DateTime SupportExpiryUtc { get; set; }

        /// <summary>Maksimum makine sayýsý</summary>
        [JsonPropertyName("maxMachines")]
        public int MaxMachines { get; set; } = 1;

        /// <summary>Aktif makine aktivasyonlarý</summary>
        [JsonPropertyName("activations")]
        public List<HardwareActivation> Activations { get; set; } = new();

        /// <summary>Çevrimdýþý grace period (gün)</summary>
        [JsonPropertyName("offlineGraceDays")]
        public int OfflineGraceDays { get; set; } = 7;

        /// <summary>Son online doðrulama zamaný (UTC)</summary>
        [JsonPropertyName("lastValidationUtc")]
        public DateTime LastValidationUtc { get; set; }

        /// <summary>RSA dijital imza</summary>
        [JsonPropertyName("signature")]
        public string Signature { get; set; } = "";

        /// <summary>Ek metadata</summary>
        [JsonPropertyName("metadata")]
        public Dictionary<string, string> Metadata { get; set; } = new();

        #region Computed Properties

        /// <summary>Trial lisansý mý?</summary>
        [JsonIgnore]
        public bool IsTrial => Type == LicenseType.Trial;

        /// <summary>Lifetime lisansý mý?</summary>
        [JsonIgnore]
        public bool IsLifetime => Type == LicenseType.Lifetime;

        /// <summary>Lisans süresi dolmuþ mu? (Trial için geçerli, Lifetime asla dolmaz)</summary>
        [JsonIgnore]
        public bool IsExpired => DateTime.UtcNow > ExpiresAtUtc;

        /// <summary>Kalan gün sayýsý (Trial için)</summary>
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

        /// <summary>Bakým/Destek aktif mi?</summary>
        [JsonIgnore]
        public bool IsSupportActive => DateTime.UtcNow <= SupportExpiryUtc;

        /// <summary>Bakým/Destek için kalan gün sayýsý</summary>
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
        /// Ýmzalanacak içeriði döndürür.
        /// Ýmza hesaplamasý için kullanýlýr.
        /// </summary>
        public string GetSignableContent()
        {
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

            foreach (var activation in Activations)
            {
                sb.Append('|');
                sb.Append(activation.HardwareId);
            }

            return sb.ToString();
        }

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
        [JsonPropertyName("hardwareId")]
        public string HardwareId { get; set; } = "";

        [JsonPropertyName("hardwareIdShort")]
        public string HardwareIdShort { get; set; } = "";

        [JsonPropertyName("machineName")]
        public string MachineName { get; set; } = "";

        [JsonPropertyName("activatedAtUtc")]
        public DateTime ActivatedAtUtc { get; set; }

        [JsonPropertyName("lastSeenUtc")]
        public DateTime LastSeenUtc { get; set; }

        [JsonPropertyName("componentsHash")]
        public string ComponentsHash { get; set; } = "";

        [JsonPropertyName("ipAddress")]
        public string? IpAddress { get; set; }

        [JsonPropertyName("osVersion")]
        public string? OsVersion { get; set; }
    }

    /// <summary>
    /// Lisans türleri.
    /// </summary>
    public enum LicenseType
    {
        Trial = 0,
        Lifetime = 1
    }

    /// <summary>
    /// Lisans doðrulama durumu.
    /// </summary>
    public enum LicenseStatus
    {
        Valid = 0,
        NotFound = 1,
        Expired = 2,
        HardwareMismatch = 3,
        InvalidSignature = 4,
        Revoked = 5,
        Tampered = 6,
        MachineLimitExceeded = 7,
        ServerUnreachable = 8,
        GracePeriod = 9,
        SupportExpired = 10,
        Unknown = 99
    }

    /// <summary>
    /// Lisans doðrulama sonucu.
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
                IsValid = true,
                Status = LicenseStatus.GracePeriod,
                Message = $"Çevrimdýþý mod - {daysRemaining} gün kaldý",
                License = license,
                GraceDaysRemaining = daysRemaining
            };
        }

        public static LicenseValidationResult SupportExpired(LicenseData license)
        {
            return new LicenseValidationResult
            {
                IsValid = true,
                Status = LicenseStatus.SupportExpired,
                Message = "Bakým/destek süreniz doldu.",
                License = license
            };
        }

        public override string ToString()
        {
            return $"[{Status}] {(IsValid ? "?" : "?")} {Message}";
        }
    }
}