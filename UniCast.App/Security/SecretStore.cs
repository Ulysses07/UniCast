using System;
using System.Security.Cryptography;
using System.Text;

namespace UniCast.App.Security
{
    /// <summary>
    /// Hassas verileri Windows DPAPI ile şifreler.
    /// DÜZELTME: Makine-spesifik entropy kullanır (sabit salt yerine).
    /// </summary>
    public static class SecretStore
    {
        // DÜZELTME: Makine-spesifik entropy (lazy initialization)
        private static readonly Lazy<byte[]> _entropy = new(GenerateMachineEntropy);

        /// <summary>
        /// Makineye özgü entropy üretir.
        /// Bu sayede şifrelenmiş veri başka makinede çözülemez.
        /// </summary>
        private static byte[] GenerateMachineEntropy()
        {
            try
            {
                // Makine kimliğinden türetilmiş benzersiz değer
                var machineId = GetMachineIdentifier();

                // SHA256 ile sabit uzunlukta hash
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(machineId));

                // İlk 16 byte yeterli
                var entropy = new byte[16];
                Array.Copy(hash, entropy, 16);

                return entropy;
            }
            catch
            {
                // Fallback: Sabit entropy (eski davranış)
                return Encoding.UTF8.GetBytes("UniCast-Fallback-2025");
            }
        }

        /// <summary>
        /// Makineyi benzersiz tanımlayan string üretir.
        /// </summary>
        private static string GetMachineIdentifier()
        {
            var sb = new StringBuilder();

            // 1. Makine adı
            sb.Append(Environment.MachineName);

            // 2. Windows kullanıcı adı (domain dahil)
            sb.Append(Environment.UserDomainName);
            sb.Append(Environment.UserName);

            // 3. İşlemci sayısı ve OS versiyonu
            sb.Append(Environment.ProcessorCount);
            sb.Append(Environment.OSVersion.VersionString);

            // 4. Sistem klasörü yolu (genellikle C:\Windows)
            sb.Append(Environment.SystemDirectory);

            // 5. Uygulama sabiti (versiyon değişse bile aynı kalsın)
            sb.Append("UniCast-v1-SecretStore");

            return sb.ToString();
        }

        /// <summary>
        /// Düz metni şifreler.
        /// </summary>
        /// <param name="plainText">Şifrelenecek metin</param>
        /// <returns>Base64 encoded şifreli veri veya null</returns>
        public static string? Protect(string? plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return null;

            try
            {
                var bytes = Encoding.UTF8.GetBytes(plainText);
                var encrypted = ProtectedData.Protect(bytes, _entropy.Value, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encrypted);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SecretStore] Protect hatası: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Şifreli metni çözer.
        /// </summary>
        /// <param name="encryptedText">Base64 encoded şifreli veri</param>
        /// <returns>Düz metin veya null</returns>
        public static string? Unprotect(string? encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText)) return null;

            try
            {
                var bytes = Convert.FromBase64String(encryptedText);
                var decrypted = ProtectedData.Unprotect(bytes, _entropy.Value, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch (CryptographicException)
            {
                // Farklı makine veya kullanıcı - eski veriyi temizle
                System.Diagnostics.Debug.WriteLine("[SecretStore] Şifre çözme başarısız (farklı makine/kullanıcı?)");
                return null;
            }
            catch (FormatException)
            {
                // Geçersiz Base64
                System.Diagnostics.Debug.WriteLine("[SecretStore] Geçersiz Base64 formatı");
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SecretStore] Unprotect hatası: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Verinin bu makinede çözülüp çözülemeyeceğini test eder.
        /// </summary>
        public static bool CanDecrypt(string? encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText)) return false;

            try
            {
                var bytes = Convert.FromBase64String(encryptedText);
                ProtectedData.Unprotect(bytes, _entropy.Value, DataProtectionScope.CurrentUser);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Mevcut entropy'nin hash'ini döndürür (debug için).
        /// Gerçek entropy'yi ASLA expose etme!
        /// </summary>
        public static string GetEntropyFingerprint()
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(_entropy.Value);
            return Convert.ToHexString(hash)[..16]; // İlk 16 karakter
        }
    }
}