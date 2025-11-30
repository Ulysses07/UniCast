using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;
using UniCast.LicenseServer.Models;

namespace UniCast.LicenseServer.Services
{
    public interface ILicenseService
    {
        Task<ActivationResponse> ActivateAsync(ActivationRequest request, string clientIp);
        Task<bool> DeactivateAsync(DeactivationRequest request);
        Task<ValidationResponse> ValidateAsync(ValidationRequest request);
        Task<LicenseData> CreateLicenseAsync(CreateLicenseRequest request);
        Task<bool> RevokeLicenseAsync(string licenseId);
        Task<IEnumerable<LicenseData>> GetAllLicensesAsync();
    }

    public class LicenseService : ILicenseService
    {
        private readonly ILicenseRepository _repository;
        private readonly string _privateKeyPath;

        public LicenseService(ILicenseRepository repository)
        {
            _repository = repository;
            _privateKeyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Keys", "license_private.pem");

            // Key dosyası yoksa oluştur
            EnsureKeysExist();
        }

        public async Task<ActivationResponse> ActivateAsync(ActivationRequest request, string clientIp)
        {
            // 1. Lisans key'ini bul
            var license = await _repository.FindByKeyAsync(request.LicenseKey);

            if (license == null)
            {
                return new ActivationResponse(false, "Geçersiz lisans anahtarı", null);
            }

            // 2. İptal edilmiş mi?
            if (license.IsRevoked)
            {
                return new ActivationResponse(false, "Bu lisans iptal edilmiş", null);
            }

            // 3. Süresi dolmuş mu?
            if (license.IsExpired)
            {
                return new ActivationResponse(false, "Lisans süresi dolmuş", null);
            }

            // 4. Bu donanım zaten kayıtlı mı?
            var existingActivation = license.Activations.Find(a => a.HardwareId == request.HardwareId);

            if (existingActivation != null)
            {
                // Zaten aktif, sadece güncelle
                existingActivation.LastSeenUtc = DateTime.UtcNow;
                existingActivation.IpAddress = clientIp;
                await _repository.SaveAsync(license);

                // İmzala ve döndür
                license.Signature = SignLicense(license);
                return new ActivationResponse(true, "Lisans zaten aktif", license);
            }

            // 5. Makine limiti kontrolü
            if (license.Activations.Count >= license.MaxMachines)
            {
                return new ActivationResponse(false,
                    $"Maksimum makine sayısına ({license.MaxMachines}) ulaşıldı", null);
            }

            // 6. Yeni aktivasyon ekle
            var activation = new HardwareActivation
            {
                HardwareId = request.HardwareId,
                HardwareIdShort = request.HardwareIdShort,
                MachineName = request.MachineName,
                ComponentsHash = request.ComponentsHash,
                ActivatedAtUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
                IpAddress = clientIp,
                OsVersion = request.OsVersion
            };

            license.Activations.Add(activation);
            await _repository.SaveAsync(license);

            // 7. İmzala ve döndür
            license.Signature = SignLicense(license);

            Log.Information("[LicenseService] Aktivasyon başarılı: {LicenseId}, Machine: {Machine}",
                license.LicenseId, request.MachineName);

            return new ActivationResponse(true, "Aktivasyon başarılı", license);
        }

        public async Task<bool> DeactivateAsync(DeactivationRequest request)
        {
            var license = await _repository.FindByIdAsync(request.LicenseId);

            if (license == null)
            {
                return false;
            }

            var activation = license.Activations.Find(a => a.HardwareId == request.HardwareId);

            if (activation == null)
            {
                return false;
            }

            license.Activations.Remove(activation);
            await _repository.SaveAsync(license);

            Log.Information("[LicenseService] Deaktivasyon başarılı: {LicenseId}, HW: {HW}",
                license.LicenseId, request.HardwareId[..Math.Min(16, request.HardwareId.Length)]);

            return true;
        }

        public async Task<ValidationResponse> ValidateAsync(ValidationRequest request)
        {
            var license = await _repository.FindByIdAsync(request.LicenseId);

            if (license == null)
            {
                return new ValidationResponse(false, LicenseStatus.NotFound, "Lisans bulunamadı");
            }

            if (license.IsRevoked)
            {
                return new ValidationResponse(false, LicenseStatus.Revoked, "Lisans iptal edilmiş");
            }

            if (license.IsExpired)
            {
                return new ValidationResponse(false, LicenseStatus.Expired, "Lisans süresi dolmuş");
            }

            var activation = license.Activations.Find(a => a.HardwareId == request.HardwareId);

            if (activation == null)
            {
                return new ValidationResponse(false, LicenseStatus.HardwareMismatch,
                    "Bu donanım için aktivasyon bulunamadı");
            }

            // Son görülme zamanını güncelle
            activation.LastSeenUtc = DateTime.UtcNow;
            await _repository.SaveAsync(license);

            return new ValidationResponse(true, LicenseStatus.Valid, "Geçerli");
        }

        public async Task<LicenseData> CreateLicenseAsync(CreateLicenseRequest request)
        {
            var licenseKey = GenerateLicenseKey();
            var licenseType = Enum.Parse<LicenseType>(request.Type, true);

            var license = new LicenseData
            {
                LicenseId = Guid.NewGuid().ToString("N"),
                LicenseKey = licenseKey,
                Type = licenseType,
                Features = GetFeaturesForType(licenseType),
                LicenseeName = request.Name,
                LicenseeEmail = request.Email,
                IssuedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(request.DurationDays),
                MaxMachines = request.MaxMachines,
                OfflineGraceDays = 7,
                Activations = new List<HardwareActivation>()
            };

            await _repository.SaveAsync(license);

            Log.Information("[LicenseService] Yeni lisans oluşturuldu: {LicenseId}, Key: {Key}, Type: {Type}",
                license.LicenseId, MaskKey(licenseKey), licenseType);

            return license;
        }

        public async Task<bool> RevokeLicenseAsync(string licenseId)
        {
            var license = await _repository.FindByIdAsync(licenseId);

            if (license == null)
            {
                return false;
            }

            license.IsRevoked = true;
            license.RevokedAtUtc = DateTime.UtcNow;
            await _repository.SaveAsync(license);

            Log.Information("[LicenseService] Lisans iptal edildi: {LicenseId}", licenseId);
            return true;
        }

        public async Task<IEnumerable<LicenseData>> GetAllLicensesAsync()
        {
            return await _repository.GetAllAsync();
        }

        #region Helpers

        private string SignLicense(LicenseData license)
        {
            try
            {
                if (!File.Exists(_privateKeyPath))
                {
                    Log.Warning("[LicenseService] Private key bulunamadı, imzalama atlanıyor");
                    return "UNSIGNED";
                }

                var privateKey = File.ReadAllText(_privateKeyPath);
                var dataToSign = license.GetSignableContent();
                var dataBytes = Encoding.UTF8.GetBytes(dataToSign);

                using var rsa = RSA.Create();
                rsa.ImportFromPem(privateKey);

                var signatureBytes = rsa.SignData(
                    dataBytes,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                return Convert.ToBase64String(signatureBytes);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LicenseService] İmzalama hatası");
                return "SIGN_ERROR";
            }
        }

        private string GenerateLicenseKey()
        {
            const string validChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            const int segmentLength = 5;
            const int segmentCount = 5;

            var segments = new string[segmentCount];

            using var rng = RandomNumberGenerator.Create();
            var bytes = new byte[segmentLength * (segmentCount - 1)];
            rng.GetBytes(bytes);

            for (int s = 0; s < segmentCount - 1; s++)
            {
                var segment = new char[segmentLength];
                for (int c = 0; c < segmentLength; c++)
                {
                    var idx = bytes[s * segmentLength + c] % validChars.Length;
                    segment[c] = validChars[idx];
                }
                segments[s] = new string(segment);
            }

            // Checksum segment
            var baseKey = string.Join("", segments.Take(segmentCount - 1));
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(baseKey + "UniCastLicenseChecksum2025"));
            var checksum = new char[segmentLength];
            for (int i = 0; i < segmentLength; i++)
                checksum[i] = validChars[hash[i] % validChars.Length];
            segments[segmentCount - 1] = new string(checksum);

            return string.Join("-", segments);
        }

        private static LicenseFeatures GetFeaturesForType(LicenseType type)
        {
            return type switch
            {
                LicenseType.Trial => LicenseFeatures.TrialFeatures,
                LicenseType.Personal => LicenseFeatures.StandardFeatures,
                LicenseType.Professional => LicenseFeatures.ProFeatures,
                LicenseType.Business => LicenseFeatures.ProFeatures,
                LicenseType.Enterprise => LicenseFeatures.AllFeatures,
                LicenseType.Lifetime => LicenseFeatures.AllFeatures,
                _ => LicenseFeatures.TrialFeatures
            };
        }

        private void EnsureKeysExist()
        {
            var keysDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Keys");
            Directory.CreateDirectory(keysDir);

            var privateKeyPath = Path.Combine(keysDir, "license_private.pem");
            var publicKeyPath = Path.Combine(keysDir, "license_public.pem");

            if (!File.Exists(privateKeyPath) || !File.Exists(publicKeyPath))
            {
                Log.Information("[LicenseService] RSA key pair oluşturuluyor...");

                using var rsa = RSA.Create(2048);
                var privateKey = rsa.ExportRSAPrivateKeyPem();
                var publicKey = rsa.ExportSubjectPublicKeyInfoPem();

                File.WriteAllText(privateKeyPath, privateKey);
                File.WriteAllText(publicKeyPath, publicKey);

                Log.Information("[LicenseService] RSA key pair oluşturuldu");
                Log.Warning("[LicenseService] ÖNEMLİ: Public key'i client uygulamasına kopyalayın!");
                Log.Information("[LicenseService] Public key path: {Path}", publicKeyPath);
            }
        }

        private static string MaskKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length < 10)
                return "***";
            return key[..5] + "-***-" + key[^5..];
        }

        #endregion
    }

    // Response modelleri
    public record ActivationResponse(bool Success, string? Message, LicenseData? License);
    public record ValidationResponse(bool Valid, LicenseStatus Status, string? Message);

    // Lisans modelleri (client ile paylaşılan)
    public class LicenseData
    {
        public string LicenseId { get; set; } = "";
        public string LicenseKey { get; set; } = "";
        public LicenseType Type { get; set; }
        public LicenseFeatures Features { get; set; }
        public string LicenseeName { get; set; } = "";
        public string LicenseeEmail { get; set; } = "";
        public DateTime IssuedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public int MaxMachines { get; set; } = 1;
        public int OfflineGraceDays { get; set; } = 7;
        public List<HardwareActivation> Activations { get; set; } = new();
        public string Signature { get; set; } = "";
        public bool IsRevoked { get; set; }
        public DateTime? RevokedAtUtc { get; set; }
        public DateTime LastValidationUtc { get; set; }

        public bool IsExpired => DateTime.UtcNow > ExpiresAtUtc;
        public bool IsTrial => Type == LicenseType.Trial;

        public string GetSignableContent()
        {
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

            foreach (var activation in Activations)
            {
                sb.Append('|');
                sb.Append(activation.HardwareId);
            }

            return sb.ToString();
        }
    }

    public class HardwareActivation
    {
        public string HardwareId { get; set; } = "";
        public string HardwareIdShort { get; set; } = "";
        public string MachineName { get; set; } = "";
        public string ComponentsHash { get; set; } = "";
        public DateTime ActivatedAtUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public string? IpAddress { get; set; }
        public string? OsVersion { get; set; }
    }

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
        NFR = 40
    }

    [Flags]
    public enum LicenseFeatures
    {
        None = 0,
        BasicStreaming = 1 << 0,
        MultiPlatform = 1 << 1,
        ChatIntegration = 1 << 2,
        Overlay = 1 << 3,
        Recording = 1 << 4,
        CustomBranding = 1 << 5,
        AdvancedAnalytics = 1 << 6,
        MultiCam = 1 << 7,
        VirtualCamera = 1 << 8,
        RTMP = 1 << 9,
        SRT = 1 << 10,
        CloudStorage = 1 << 11,
        TeamCollaboration = 1 << 12,
        APIAccess = 1 << 13,
        WhiteLabel = 1 << 14,
        PrioritySupport = 1 << 15,

        TrialFeatures = BasicStreaming | MultiPlatform | ChatIntegration | Overlay,
        StandardFeatures = TrialFeatures | Recording | CustomBranding,
        ProFeatures = StandardFeatures | AdvancedAnalytics | MultiCam | VirtualCamera | RTMP | SRT,
        AllFeatures = int.MaxValue
    }

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
        GracePeriod = 9
    }
}