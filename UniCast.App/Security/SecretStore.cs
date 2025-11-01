using System;
using System.Security.Cryptography;
using System.Text;

namespace UniCast.App.Security
{
    /// <summary>
    /// Windows DPAPI (CurrentUser) ile local makinede şifreleme.
    /// JSON'a base64 olarak yazarız; çözülürken otomatik açarız.
    /// </summary>
    public static class SecretStore
    {
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            var bytes = Encoding.UTF8.GetBytes(plain);
            var enc = ProtectedData.Protect(bytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }

        public static string Unprotect(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return "";
            try
            {
                var enc = Convert.FromBase64String(base64);
                var dec = ProtectedData.Unprotect(enc, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch { return ""; }
        }
    }
}
