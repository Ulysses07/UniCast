using System;
using System.Security.Cryptography;
using System.Text;

namespace UniCast.App.Security
{
    public static class SecretStore
    {
        // EK GÜVENLİK KATMANI (ENTROPY)
        // Bu byte dizisi olmadan şifre çözülemez.
        private static readonly byte[] _entropy = Encoding.UTF8.GetBytes("UniCast-Secure-Salt-2025-v1");

        public static string? Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return null;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(plainText);
                // Entropy eklendi
                var encrypted = ProtectedData.Protect(bytes, _entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encrypted);
            }
            catch { return null; }
        }

        public static string? Unprotect(string? encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText)) return null;
            try
            {
                var bytes = Convert.FromBase64String(encryptedText);
                // Entropy ile çözülüyor
                var decrypted = ProtectedData.Unprotect(bytes, _entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch { return null; }
        }
    }
}