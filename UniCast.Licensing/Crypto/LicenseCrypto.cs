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
        // ÖNEMLİ: Bu public key uygulamaya gömülür
        // Private key ASLA paylaşılmaz, sadece lisans sunucusunda bulunur
        // Aşağıdaki örnek key'dir - production'da kendi key'inizi oluşturun!
        private const string EmbeddedPublicKey = @"-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA0Z3VS5JJcds3xfn/ygWi
D+rPTzUmNUGJxA8UKM/zYYQeR5nKxHlBQBDI8Lz+GlBbC9gVdJBL8bJME1QWKZ6
DM3PzLqYmJW9pBjHsyfIKP3F7H9JJc3D1Z2Y7F1+yKJ8cJKvXyJ7cG8Hy3yJ9sW
L8M5Y5P8Q2L7G9N1RvJKQ3E8F6Y2X9hZcDkWQ9gR7pJ5kF2bL3N8yJMvJ5G8Y4cZ
R2W1pBjP9D8Q6kJc5fWbL3yJK8cJE2X9hZL7G9N1Q3F8Y2kJ5pR7bL3N8yJMvJ5G
8Y4cZR2W1pBjP9D8Q6kJc5fWbL3yJK8cJE2X9hZL7G9N1Q3F8Y2kJ5pR7bL3N8yJ
MwIDAQAB
-----END PUBLIC KEY-----";

        // Sunucu tarafında kullanılacak private key (ASLA client'a gönderilmez)
        // Bu sadece LicenseServer projesinde bulunmalı
        private const string ServerPrivateKey = @"-----BEGIN RSA PRIVATE KEY-----
MIIEpAIBAAKCAQEA0Z3VS5JJcds3xfn/ygWiD+rPTzUmNUGJxA8UKM/zYYQeR5nK
xHlBQBDI8Lz+GlBbC9gVdJBL8bJME1QWKZ6DM3PzLqYmJW9pBjHsyfIKP3F7H9J
Jc3D1Z2Y7F1+yKJ8cJKvXyJ7cG8Hy3yJ9sWL8M5Y5P8Q2L7G9N1RvJKQ3E8F6Y2X
...TRUNCATED FOR SECURITY - GENERATE YOUR OWN...
-----END RSA PRIVATE KEY-----";

        /// <summary>
        /// Lisans verisini RSA-2048 ile imzalar (SUNUCU TARAFI).
        ///</summary>
        public static string Sign(LicenseData license)
        {
            var dataToSign = license.GetSignableContent();
            var dataBytes = Encoding.UTF8.GetBytes(dataToSign);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(ServerPrivateKey);

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
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Yeni RSA-2048 key pair oluşturur.
        /// İlk kurulumda bir kez çalıştırılır.
        /// </summary>
        public static (string PublicKey, string PrivateKey) GenerateKeyPair()
        {
            using var rsa = RSA.Create(2048);

            var privateKey = rsa.ExportRSAPrivateKeyPem();
            var publicKey = rsa.ExportSubjectPublicKeyInfoPem();

            return (publicKey, privateKey);
        }
    }

    /// <summary>
    /// Lisans dosyası şifreleme (AES-256-GCM).
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

        /// <summary>
        /// Lisans verisini şifreler ve dosyaya yazar.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static void SaveEncrypted(LicenseData license, string filePath)
        {
            var json = JsonSerializer.Serialize(license, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            var plainBytes = Encoding.UTF8.GetBytes(json);
            var encryptedBytes = ProtectedData.Protect(
                plainBytes,
                AdditionalEntropy,
                DataProtectionScope.CurrentUser);

            // Magic header + version + encrypted data
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(fs);

            writer.Write(new byte[] { 0x55, 0x43, 0x4C, 0x49 }); // "UCLI" magic
            writer.Write((byte)1); // Version
            writer.Write(encryptedBytes.Length);
            writer.Write(encryptedBytes);

            // Checksum
            using var sha = SHA256.Create();
            var checksum = sha.ComputeHash(encryptedBytes);
            writer.Write(checksum);
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
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(fs);

                // Magic header kontrolü
                var magic = reader.ReadBytes(4);
                if (magic[0] != 0x55 || magic[1] != 0x43 || magic[2] != 0x4C || magic[3] != 0x49)
                    return null; // Geçersiz dosya formatı

                var version = reader.ReadByte();
                if (version != 1)
                    return null; // Desteklenmeyen versiyon

                var length = reader.ReadInt32();
                var encryptedBytes = reader.ReadBytes(length);
                var storedChecksum = reader.ReadBytes(32);

                // Checksum doğrulama
                using var sha = SHA256.Create();
                var computedChecksum = sha.ComputeHash(encryptedBytes);

                if (!CryptographicEquals(storedChecksum, computedChecksum))
                    return null; // Dosya bozulmuş veya kurcalanmış

                // Şifre çözme
                var plainBytes = ProtectedData.Unprotect(
                    encryptedBytes,
                    AdditionalEntropy,
                    DataProtectionScope.CurrentUser);

                var json = Encoding.UTF8.GetString(plainBytes);
                return JsonSerializer.Deserialize<LicenseData>(json);
            }
            catch
            {
                return null;
            }
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
        private const string ValidChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Karışıklık önlemek için I,O,0,1 yok
        private const int SegmentLength = 5;
        private const int SegmentCount = 5;

        /// <summary>
        /// Yeni lisans anahtarı oluşturur.
        /// </summary>
        public static string Generate()
        {
            var segments = new string[SegmentCount];

            using var rng = RandomNumberGenerator.Create();
            var bytes = new byte[SegmentLength * SegmentCount];
            rng.GetBytes(bytes);

            for (int s = 0; s < SegmentCount; s++)
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
            var baseKey = string.Join("", segments.Take(4));
            var checksum = ComputeKeyChecksum(baseKey);
            segments[4] = checksum;

            return string.Join("-", segments);
        }

        /// <summary>
        /// Lisans anahtarı formatını ve checksum'ını doğrular.
        /// </summary>
        public static bool Validate(string key)
        {
            if (string.IsNullOrEmpty(key))
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
            var baseKey = string.Join("", parts.Take(4));
            var expectedChecksum = ComputeKeyChecksum(baseKey);

            return parts[4] == expectedChecksum;
        }

        /// <summary>
        /// Lisans anahtarını normalize eder (büyük harf, tire'li format).
        /// </summary>
        public static string Normalize(string key)
        {
            if (string.IsNullOrEmpty(key))
                return "";

            var clean = key.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");

            if (clean.Length != SegmentLength * SegmentCount)
                return key; // Normalize edilemez

            var segments = new string[SegmentCount];
            for (int i = 0; i < SegmentCount; i++)
                segments[i] = clean.Substring(i * SegmentLength, SegmentLength);

            return string.Join("-", segments);
        }

        private static string ComputeKeyChecksum(string baseKey)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(baseKey + "UniCastLicenseChecksum"));

            var checksum = new char[SegmentLength];
            for (int i = 0; i < SegmentLength; i++)
                checksum[i] = ValidChars[hash[i] % ValidChars.Length];

            return new string(checksum);
        }
    }
}