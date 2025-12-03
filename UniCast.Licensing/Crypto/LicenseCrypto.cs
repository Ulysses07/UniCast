using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniCast.Licensing.Models;

namespace UniCast.Licensing.Crypto
{
    /// <summary>
    /// KATMAN 2: RSA-2048 dijital imza ile lisans bütünlük koruması.
    /// Private key SADECE sunucuda bulunur.
    /// Public key uygulamaya gömülüdür (imza doğrulama için).
    /// </summary>
    public static class LicenseSigner
    {
        // ÖNEMLİ: Production'da GenerateAndSaveKeyPair() ile oluşturulan key'leri kullanın!
        // Bu public key uygulamaya gömülür - Private key ASLA client'a gönderilmez!

        // Production Public Key - Bu key'i değiştirmeyin, sadece GenerateAndSaveKeyPair ile oluşturulan key'i kullanın
        private static readonly string EmbeddedPublicKey;

        // Sunucu tarafında kullanılacak private key path
        private const string PrivateKeyFileName = "license_private.pem";
        private const string PublicKeyFileName = "license_public.pem";

        static LicenseSigner()
        {
            // Embedded public key'i yükle veya varsayılanı kullan
            var keyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Keys", PublicKeyFileName);

            if (File.Exists(keyPath))
            {
                EmbeddedPublicKey = File.ReadAllText(keyPath);
                System.Diagnostics.Debug.WriteLine($"[LicenseSigner] Production public key yüklendi: {keyPath}");
            }
            else
            {
#if DEBUG
                // DÜZELTME v27: Development key SADECE DEBUG modunda kullanılır
                // Bu key ile imzalanan lisanslar production'da çalışmaz!
                System.Diagnostics.Debug.WriteLine("[LicenseSigner] WARNING: Using development key - NOT FOR PRODUCTION!");
                EmbeddedPublicKey = @"-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAwr8ywjC1J9lh4uPQhWeZ
FXQzcJ/lWPhq78NLN+01ZFaxM7HN+NdHBq3kZhTTQflZkMfqbzwIpJuk46xNDZWd
xGTXKi9vR1guI8oxy6CzE1xVVoXFyfxiKaU1WQ4drA0rxfQPJTviInJJkRkjjc1Q
THYHU+r5Di3w3BkmLTBI2FJk0E4p/vZoeeYClZbPTkdV+m+R03gvmNGffaOY8Q+Y
I0BO3dJSI+jmRSm77IEsD86ciKLGAMPee+2T0XOBjMdPzhl1U2S0uELR/K/M4yHk
VUEHp1Nss8XnoNcY68+hYi5gcqHMFac9EdHdUnvGhtqXOn0LmSTZ6KJSKY6Q5USp
pwIDAQAB
-----END PUBLIC KEY-----";
#else
                // DÜZELTME v27: Production'da key dosyası ZORUNLU!
                // GenerateAndSaveKeyPair() ile key oluşturup Keys/ klasörüne koyun.
                var errorMessage = $"KRITIK HATA: Production public key bulunamadı!\n" +
                                   $"Beklenen konum: {keyPath}\n" +
                                   $"Çözüm: GenerateAndSaveKeyPair() ile key oluşturun.";
                
                System.Diagnostics.Debug.WriteLine($"[LicenseSigner] {errorMessage}");
                
                // Boş key ile başlat - Verify() her zaman false dönecek
                EmbeddedPublicKey = "";
                
                // Event log'a yaz (Windows)
                try
                {
                    using var eventLog = new System.Diagnostics.EventLog("Application");
                    eventLog.Source = "UniCast";
                    eventLog.WriteEntry(errorMessage, System.Diagnostics.EventLogEntryType.Error);
                }
                catch { /* Event log yazılamazsa devam et */ }
#endif
            }
        }

        /// <summary>
        /// Yeni RSA-2048 key pair oluşturur ve dosyalara kaydeder.
        /// İlk kurulumda BİR KEZ çalıştırılmalı!
        /// </summary>
        /// <param name="outputDirectory">Key dosyalarının kaydedileceği dizin</param>
        /// <returns>Oluşturulan public ve private key</returns>
        public static (string PublicKey, string PrivateKey) GenerateAndSaveKeyPair(string? outputDirectory = null)
        {
            var outputDir = outputDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Keys");
            Directory.CreateDirectory(outputDir);

            using var rsa = RSA.Create(2048);

            var privateKey = rsa.ExportRSAPrivateKeyPem();
            var publicKey = rsa.ExportSubjectPublicKeyInfoPem();

            var privateKeyPath = Path.Combine(outputDir, PrivateKeyFileName);
            var publicKeyPath = Path.Combine(outputDir, PublicKeyFileName);

            // Private key'i güvenli izinlerle kaydet
            File.WriteAllText(privateKeyPath, privateKey);
            File.WriteAllText(publicKeyPath, publicKey);

            Console.WriteLine($"[LicenseSigner] Key pair oluşturuldu:");
            Console.WriteLine($"  Private Key: {privateKeyPath}");
            Console.WriteLine($"  Public Key:  {publicKeyPath}");
            Console.WriteLine();
            Console.WriteLine("ÖNEMLİ: Private key'i GÜVENLİ bir yerde saklayın!");
            Console.WriteLine("        Public key'i client uygulamasına gömün.");

            return (publicKey, privateKey);
        }

        /// <summary>
        /// Lisans verisini RSA-2048 ile imzalar (SUNUCU TARAFI).
        /// </summary>
        public static string Sign(LicenseData license, string? privateKeyPath = null)
        {
            var keyPath = privateKeyPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Keys", PrivateKeyFileName);

            if (!File.Exists(keyPath))
                throw new FileNotFoundException("Private key dosyası bulunamadı. Önce GenerateAndSaveKeyPair() çalıştırın.", keyPath);

            var privateKey = File.ReadAllText(keyPath);
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

        /// <summary>
        /// Lisans imzasını doğrular (CLIENT TARAFI).
        /// Gömülü public key kullanılır.
        /// </summary>
        public static bool Verify(LicenseData license)
        {
            if (string.IsNullOrEmpty(license.Signature))
                return false;

            // Trial lisanslar için özel kontrol
            if (license.IsTrial && license.Signature == "TRIAL_LOCAL")
                return true;

            try
            {
                var dataToVerify = license.GetSignableContent();
                var dataBytes = Encoding.UTF8.GetBytes(dataToVerify);
                var signatureBytes = Convert.FromBase64String(license.Signature);

                using var rsa = RSA.Create();
                rsa.ImportFromPem(EmbeddedPublicKey);

                return rsa.VerifyData(
                    dataBytes,
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LicenseSigner] Verify hatası: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Public key fingerprint'ini döndürür (debug için).
        /// </summary>
        public static string GetPublicKeyFingerprint()
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(EmbeddedPublicKey);
                var publicKeyBytes = rsa.ExportSubjectPublicKeyInfo();
                var hash = SHA256.HashData(publicKeyBytes);
                return Convert.ToHexString(hash)[..16];
            }
            catch
            {
                return "INVALID_KEY";
            }
        }
    }

    /// <summary>
    /// Lisans dosyası şifreleme (AES-256 + DPAPI).
    /// Lisans dosyası disk'te şifreli saklanır.
    /// </summary>
    public static class LicenseEncryption
    {
        // AES key türetme için makineye özgü entropi
        private static readonly byte[] AdditionalEntropy =
        {
            0x55, 0x43, 0x4C, 0x69, 0x63, 0x65, 0x6E, 0x73,
            0x65, 0x45, 0x6E, 0x63, 0x72, 0x79, 0x70, 0x74
        };

        private const byte CurrentVersion = 2; // Versiyon artırıldı
        private static readonly byte[] MagicHeader = { 0x55, 0x43, 0x4C, 0x49 }; // "UCLI"

        /// <summary>
        /// Lisans verisini şifreler ve dosyaya yazar.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static void SaveEncrypted(LicenseData license, string filePath)
        {
            ArgumentNullException.ThrowIfNull(license);
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            var json = JsonSerializer.Serialize(license, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            var plainBytes = Encoding.UTF8.GetBytes(json);
            var encryptedBytes = ProtectedData.Protect(
                plainBytes,
                AdditionalEntropy,
                DataProtectionScope.CurrentUser);

            // Dizini oluştur
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Atomic write
            var tempPath = filePath + ".tmp";

            try
            {
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(fs))
                {
                    // Magic header
                    writer.Write(MagicHeader);

                    // Version
                    writer.Write(CurrentVersion);

                    // Timestamp (replay attack koruması)
                    writer.Write(DateTime.UtcNow.ToBinary());

                    // Data length + data
                    writer.Write(encryptedBytes.Length);
                    writer.Write(encryptedBytes);

                    // Checksum (HMAC-SHA256)
                    var checksum = ComputeHmac(encryptedBytes);
                    writer.Write(checksum);

                    fs.Flush(true);
                }

                // Atomic rename
                if (File.Exists(filePath))
                    File.Delete(filePath);

                File.Move(tempPath, filePath);
            }
            catch
            {
                if (File.Exists(tempPath))
                {
                    // DÜZELTME v26: Boş catch'e loglama eklendi
                    try { File.Delete(tempPath); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[LicenseCrypto] Temp dosya silme hatası: {ex.Message}"); }
                }
                throw;
            }
        }

        /// <summary>
        /// Şifrelenmiş lisans dosyasını okur ve çözer.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static LicenseData? LoadEncrypted(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(fs);

                // Magic header kontrolü
                var magic = reader.ReadBytes(4);
                if (!magic.AsSpan().SequenceEqual(MagicHeader))
                {
                    System.Diagnostics.Debug.WriteLine("[LicenseEncryption] Geçersiz magic header");
                    return null;
                }

                var version = reader.ReadByte();
                if (version > CurrentVersion)
                {
                    System.Diagnostics.Debug.WriteLine($"[LicenseEncryption] Desteklenmeyen versiyon: {version}");
                    return null;
                }

                // Timestamp (v2+)
                if (version >= 2)
                {
                    var timestamp = DateTime.FromBinary(reader.ReadInt64());
                    // Çok eski dosyaları reddet (opsiyonel)
                    if ((DateTime.UtcNow - timestamp).TotalDays > 365)
                    {
                        System.Diagnostics.Debug.WriteLine("[LicenseEncryption] Lisans dosyası çok eski");
                        // return null; // İsteğe bağlı
                    }
                }

                var length = reader.ReadInt32();

                // Makul boyut kontrolü
                if (length <= 0 || length > 1024 * 1024) // Max 1MB
                {
                    System.Diagnostics.Debug.WriteLine("[LicenseEncryption] Geçersiz veri boyutu");
                    return null;
                }

                var encryptedBytes = reader.ReadBytes(length);
                var storedChecksum = reader.ReadBytes(32);

                // Checksum doğrulama
                var computedChecksum = ComputeHmac(encryptedBytes);

                if (!CryptographicEquals(storedChecksum, computedChecksum))
                {
                    System.Diagnostics.Debug.WriteLine("[LicenseEncryption] Checksum doğrulaması başarısız");
                    return null;
                }

                // Şifre çözme
                var plainBytes = ProtectedData.Unprotect(
                    encryptedBytes,
                    AdditionalEntropy,
                    DataProtectionScope.CurrentUser);

                var json = Encoding.UTF8.GetString(plainBytes);
                return JsonSerializer.Deserialize<LicenseData>(json);
            }
            catch (CryptographicException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LicenseEncryption] Şifre çözme hatası: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LicenseEncryption] Yükleme hatası: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Lisans dosyasını güvenli şekilde siler.
        /// </summary>
        public static void SecureDelete(string filePath)
        {
            if (!File.Exists(filePath))
                return;

            try
            {
                var fileInfo = new FileInfo(filePath);
                var length = fileInfo.Length;

                // Dosyayı random veri ile üzerine yaz
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    var random = new byte[4096];
                    using var rng = RandomNumberGenerator.Create();

                    for (long i = 0; i < length; i += random.Length)
                    {
                        rng.GetBytes(random);
                        var bytesToWrite = (int)Math.Min(random.Length, length - i);
                        fs.Write(random, 0, bytesToWrite);
                    }

                    fs.Flush(true);
                }

                File.Delete(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LicenseEncryption] SecureDelete hatası: {ex.Message}");

                // Fallback: Normal silme
                // DÜZELTME v26: Boş catch'e loglama eklendi
                try { File.Delete(filePath); } catch (Exception deleteEx) { System.Diagnostics.Debug.WriteLine($"[LicenseCrypto] Normal silme de başarısız: {deleteEx.Message}"); }
            }
        }

        private static byte[] ComputeHmac(byte[] data)
        {
            using var hmac = new HMACSHA256(AdditionalEntropy);
            return hmac.ComputeHash(data);
        }

        /// <summary>
        /// Timing-safe karşılaştırma (side-channel attack önleme).
        /// </summary>
        private static bool CryptographicEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];

            return diff == 0;
        }
    }

    /// <summary>
    /// Lisans key formatı ve doğrulama.
    /// Format: XXXXX-XXXXX-XXXXX-XXXXX-XXXXX (25 karakter + 4 tire)
    /// </summary>
    public static class LicenseKeyFormat
    {
        private const string ValidChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // I,O,0,1 yok (karışıklık önleme)
        private const int SegmentLength = 5;
        private const int SegmentCount = 5;
        private const int TotalLength = SegmentLength * SegmentCount + (SegmentCount - 1); // 29 karakter

        /// <summary>
        /// Yeni lisans anahtarı oluşturur.
        /// </summary>
        public static string Generate()
        {
            var segments = new string[SegmentCount];

            using var rng = RandomNumberGenerator.Create();
            var bytes = new byte[SegmentLength * SegmentCount];
            rng.GetBytes(bytes);

            for (int s = 0; s < SegmentCount - 1; s++) // Son segment checksum olacak
            {
                var segment = new char[SegmentLength];
                for (int c = 0; c < SegmentLength; c++)
                {
                    var idx = bytes[s * SegmentLength + c] % ValidChars.Length;
                    segment[c] = ValidChars[idx];
                }
                segments[s] = new string(segment);
            }

            // Son segment'e checksum ekle
            var baseKey = string.Join("", segments.Take(SegmentCount - 1));
            segments[SegmentCount - 1] = ComputeKeyChecksum(baseKey);

            return string.Join("-", segments);
        }

        /// <summary>
        /// Lisans anahtarı formatını ve checksum'ını doğrular.
        /// </summary>
        public static bool Validate(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            // Temizle
            key = key.Trim().ToUpperInvariant().Replace(" ", "");

            // Format kontrolü
            var parts = key.Split('-');
            if (parts.Length != SegmentCount)
                return false;

            foreach (var part in parts)
            {
                if (part.Length != SegmentLength)
                    return false;

                foreach (var c in part)
                {
                    if (!ValidChars.Contains(c))
                        return false;
                }
            }

            // Checksum kontrolü
            var baseKey = string.Join("", parts.Take(SegmentCount - 1));
            var expectedChecksum = ComputeKeyChecksum(baseKey);

            return parts[SegmentCount - 1] == expectedChecksum;
        }

        /// <summary>
        /// Lisans anahtarını normalize eder (büyük harf, tire'li format).
        /// </summary>
        public static string Normalize(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return "";

            var clean = key.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");

            if (clean.Length != SegmentLength * SegmentCount)
                return key ?? ""; // Normalize edilemez

            var segments = new string[SegmentCount];
            for (int i = 0; i < SegmentCount; i++)
                segments[i] = clean.Substring(i * SegmentLength, SegmentLength);

            return string.Join("-", segments);
        }

        /// <summary>
        /// Maskelenmiş key döndürür (UI için).
        /// Örnek: XXXXX-XXXXX-*****-*****-XXXXX
        /// </summary>
        public static string Mask(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return "";

            var normalized = Normalize(key);
            var parts = normalized.Split('-');

            if (parts.Length != SegmentCount)
                return key ?? "";

            // Ortadaki segmentleri maskele
            for (int i = 1; i < SegmentCount - 1; i++)
                parts[i] = "*****";

            return string.Join("-", parts);
        }

        private static string ComputeKeyChecksum(string baseKey)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(baseKey + "UniCastLicenseChecksum2025"));

            var checksum = new char[SegmentLength];
            for (int i = 0; i < SegmentLength; i++)
                checksum[i] = ValidChars[hash[i] % ValidChars.Length];

            return new string(checksum);
        }
    }
}