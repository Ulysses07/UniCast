using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace UniCast.LicenseServer.Services
{
    /// <summary>
    /// Lisans servisi interface
    /// </summary>
    public interface ILicenseService
    {
        Task<ActivationResult> ActivateAsync(string licenseKey, string hardwareId, string? machineName, string? ipAddress);
        Task<bool> DeactivateAsync(string licenseKey, string hardwareId);
        Task<ValidationResult> ValidateAsync(string licenseKey, string hardwareId);
        Task<LicenseData> CreateLicenseAsync(CreateLicenseRequest request);
        Task<bool> RevokeLicenseAsync(string licenseId);
        Task<IEnumerable<LicenseData>> GetAllLicensesAsync();
    }

    /// <summary>
    /// Lisans servisi implementasyonu
    /// </summary>
    public class LicenseService : ILicenseService
    {
        private readonly ILicenseRepository _repository;
        private readonly string _keysPath;

        public LicenseService(ILicenseRepository repository)
        {
            _repository = repository;
            _keysPath = Path.Combine(AppContext.BaseDirectory, "Keys");

            if (!Directory.Exists(_keysPath))
            {
                Directory.CreateDirectory(_keysPath);
            }

            EnsureKeysExist();
        }

        public async Task<ActivationResult> ActivateAsync(string licenseKey, string hardwareId, string? machineName, string? ipAddress)
        {
            var license = await _repository.FindByKeyAsync(licenseKey);

            if (license == null)
            {
                return ActivationResult.Failed("Lisans bulunamadı");
            }

            if (license.IsRevoked)
            {
                return ActivationResult.Failed("Bu lisans iptal edilmiş");
            }

            if (license.ExpiresAtUtc < DateTime.UtcNow)
            {
                return ActivationResult.Failed("Lisans süresi dolmuş");
            }

            // Mevcut aktivasyon kontrolü
            var existingActivation = license.Activations.FirstOrDefault(a => a.HardwareId == hardwareId);
            if (existingActivation != null)
            {
                existingActivation.LastSeenUtc = DateTime.UtcNow;
                existingActivation.IpAddress = ipAddress;
                await _repository.SaveAsync(license);

                return ActivationResult.Succeeded(license, "Mevcut aktivasyon güncellendi");
            }

            // Makine limiti kontrolü
            if (license.Activations.Count >= license.MaxMachines)
            {
                return ActivationResult.Failed($"Maksimum makine sayısına ({license.MaxMachines}) ulaşıldı");
            }

            // Yeni aktivasyon
            var activation = new HardwareActivation
            {
                HardwareId = hardwareId,
                HardwareIdShort = hardwareId.Length > 8 ? hardwareId.Substring(0, 8) : hardwareId,
                MachineName = machineName,
                ActivatedAtUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
                IpAddress = ipAddress,
                OsVersion = Environment.OSVersion.ToString()
            };

            license.Activations.Add(activation);
            license.Signature = await SignLicenseAsync(license);
            await _repository.SaveAsync(license);

            return ActivationResult.Succeeded(license, "Aktivasyon başarılı");
        }

        public async Task<bool> DeactivateAsync(string licenseKey, string hardwareId)
        {
            var license = await _repository.FindByKeyAsync(licenseKey);
            if (license == null) return false;

            var activation = license.Activations.FirstOrDefault(a => a.HardwareId == hardwareId);
            if (activation == null) return false;

            license.Activations.Remove(activation);
            await _repository.SaveAsync(license);
            return true;
        }

        public async Task<ValidationResult> ValidateAsync(string licenseKey, string hardwareId)
        {
            var license = await _repository.FindByKeyAsync(licenseKey);

            if (license == null)
            {
                return ValidationResult.Invalid("Lisans bulunamadı");
            }

            if (license.IsRevoked)
            {
                return ValidationResult.Invalid("Lisans iptal edilmiş");
            }

            if (license.ExpiresAtUtc < DateTime.UtcNow)
            {
                return ValidationResult.Invalid("Lisans süresi dolmuş");
            }

            var activation = license.Activations.FirstOrDefault(a => a.HardwareId == hardwareId);
            if (activation == null)
            {
                return ValidationResult.Invalid("Bu cihaz için aktivasyon bulunamadı");
            }

            // LastSeen güncelle
            activation.LastSeenUtc = DateTime.UtcNow;
            license.LastValidationUtc = DateTime.UtcNow;
            await _repository.SaveAsync(license);

            return ValidationResult.Valid(license);
        }

        public async Task<LicenseData> CreateLicenseAsync(CreateLicenseRequest request)
        {
            var license = new LicenseData
            {
                LicenseId = Guid.NewGuid().ToString("N"),
                LicenseKey = GenerateLicenseKey(),
                Type = request.LicenseType,
                Features = GetFeaturesForType(request.LicenseType),
                LicenseeName = request.LicenseeName,
                LicenseeEmail = request.LicenseeEmail,
                IssuedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(request.DurationDays),
                MaxMachines = request.MaxMachines,
                Activations = new List<HardwareActivation>()
            };

            license.Signature = await SignLicenseAsync(license);
            await _repository.SaveAsync(license);

            return license;
        }

        public async Task<bool> RevokeLicenseAsync(string licenseId)
        {
            var license = await _repository.FindByIdAsync(licenseId);
            if (license == null) return false;

            license.IsRevoked = true;
            license.RevokedAtUtc = DateTime.UtcNow;
            await _repository.SaveAsync(license);

            return true;
        }

        public async Task<IEnumerable<LicenseData>> GetAllLicensesAsync()
        {
            return await _repository.GetAllAsync();
        }

        private string GenerateLicenseKey()
        {
            var segments = new string[5];
            var chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Karıştırılabilir karakterler çıkarıldı
            var random = RandomNumberGenerator.Create();
            var bytes = new byte[5];

            for (int i = 0; i < 4; i++)
            {
                random.GetBytes(bytes);
                segments[i] = new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
            }

            // Son segment checksum
            var combined = string.Join("", segments.Take(4));
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(combined));
            segments[4] = new string(hash.Take(5).Select(b => chars[b % chars.Length]).ToArray());

            return string.Join("-", segments);
        }

        private long GetFeaturesForType(string type)
        {
            return type.ToLower() switch
            {
                "trial" => 0x03, // BasicStreaming | ChatIntegration
                "personal" => 0x3F, // Standard features + NoWatermark
                "professional" => 0x3FFF,
                "business" => 0xFFFFFF,
                "enterprise" => -1L, // All features
                _ => 0x03
            };
        }

        private async Task<string> SignLicenseAsync(LicenseData license)
        {
            var privateKeyPath = Path.Combine(_keysPath, "private.key");
            if (!File.Exists(privateKeyPath))
            {
                return string.Empty;
            }

            var data = $"{license.LicenseId}|{license.LicenseKey}|{license.ExpiresAtUtc:O}";
            var dataBytes = Encoding.UTF8.GetBytes(data);

            using var rsa = RSA.Create();
            var privateKey = await File.ReadAllTextAsync(privateKeyPath);
            rsa.ImportFromPem(privateKey);

            var signature = rsa.SignData(dataBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return Convert.ToBase64String(signature);
        }

        private void EnsureKeysExist()
        {
            var privateKeyPath = Path.Combine(_keysPath, "private.key");
            var publicKeyPath = Path.Combine(_keysPath, "public.key");

            if (!File.Exists(privateKeyPath) || !File.Exists(publicKeyPath))
            {
                using var rsa = RSA.Create(2048);

                var privateKey = rsa.ExportRSAPrivateKeyPem();
                var publicKey = rsa.ExportRSAPublicKeyPem();

                File.WriteAllText(privateKeyPath, privateKey);
                File.WriteAllText(publicKeyPath, publicKey);
            }
        }
    }

    // Result classes
    public class ActivationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? Message { get; set; }
        public LicenseData? License { get; set; }

        public static ActivationResult Succeeded(LicenseData license, string message) => new()
        {
            Success = true,
            License = license,
            Message = message
        };

        public static ActivationResult Failed(string error) => new()
        {
            Success = false,
            ErrorMessage = error
        };
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public string? ErrorMessage { get; set; }
        public LicenseData? License { get; set; }

        public static ValidationResult Valid(LicenseData license) => new()
        {
            IsValid = true,
            License = license
        };

        public static ValidationResult Invalid(string error) => new()
        {
            IsValid = false,
            ErrorMessage = error
        };
    }
}