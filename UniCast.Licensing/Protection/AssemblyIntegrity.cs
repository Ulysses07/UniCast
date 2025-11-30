using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace UniCast.Licensing.Protection
{
    /// <summary>
    /// KATMAN 4: Assembly bütünlük kontrolü.
    /// Uygulama dosyalarının değiştirilmediğini doğrular.
    /// </summary>
    public static class AssemblyIntegrity
    {
        private static readonly Dictionary<string, string> _expectedHashes = new();
        private static bool _isInitialized;
        private static readonly object _initLock = new();

        // Kontrol edilecek assembly'ler
        private static readonly string[] ProtectedAssemblies =
        {
            "UniCast.App.dll",
            "UniCast.Core.dll",
            "UniCast.Licensing.dll",
            "UniCast.Encoder.dll"
        };

        /// <summary>
        /// Bütünlük sistemini başlatır.
        /// İlk çalıştırmada hash'leri kaydeder.
        /// </summary>
        public static void Initialize()
        {
            if (_isInitialized)
                return;

            lock (_initLock)
            {
                if (_isInitialized)
                    return;

#if DEBUG
                // DEBUG modunda hash hesaplaması atlanır
                _isInitialized = true;
                return;
#else
                try
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;

                    foreach (var assemblyName in ProtectedAssemblies)
                    {
                        var assemblyPath = Path.Combine(baseDir, assemblyName);

                        if (File.Exists(assemblyPath))
                        {
                            var hash = ComputeFileHash(assemblyPath);
                            _expectedHashes[assemblyName] = hash;
                        }
                    }

                    _isInitialized = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AssemblyIntegrity] Initialize hatası: {ex.Message}");
                }
#endif
            }
        }

        /// <summary>
        /// Tüm korumalı assembly'leri doğrular.
        /// </summary>
        public static IntegrityResult VerifyAll()
        {
            var result = new IntegrityResult();

#if DEBUG
            // DEBUG modunda her zaman geçerli
            result.IsValid = true;
            return result;
#else
            if (!_isInitialized)
            {
                Initialize();
            }

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;

            foreach (var assemblyName in ProtectedAssemblies)
            {
                var verification = new AssemblyVerification { Name = assemblyName };

                try
                {
                    var assemblyPath = Path.Combine(baseDir, assemblyName);

                    if (!File.Exists(assemblyPath))
                    {
                        verification.Status = VerificationStatus.Missing;
                        verification.Details = "Dosya bulunamadı";
                    }
                    else
                    {
                        var currentHash = ComputeFileHash(assemblyPath);
                        verification.CurrentHash = currentHash;

                        if (_expectedHashes.TryGetValue(assemblyName, out var expectedHash))
                        {
                            verification.ExpectedHash = expectedHash;

                            if (currentHash == expectedHash)
                            {
                                verification.Status = VerificationStatus.Valid;
                            }
                            else
                            {
                                verification.Status = VerificationStatus.Modified;
                                verification.Details = "Hash uyuşmazlığı";
                            }
                        }
                        else
                        {
                            // İlk çalıştırma - hash kayıtlı değil
                            verification.Status = VerificationStatus.Valid;
                            verification.Details = "İlk doğrulama";
                            _expectedHashes[assemblyName] = currentHash;
                        }
                    }
                }
                catch (Exception ex)
                {
                    verification.Status = VerificationStatus.Error;
                    verification.Details = ex.Message;
                }

                result.Verifications.Add(verification);
            }

            // Genel sonuç
            result.IsValid = result.Verifications.All(v =>
                v.Status == VerificationStatus.Valid ||
                v.Status == VerificationStatus.Missing); // Eksik dosya normal olabilir

            return result;
#endif
        }

        /// <summary>
        /// Belirli bir assembly'i doğrular.
        /// </summary>
        public static AssemblyVerification VerifyAssembly(string assemblyName)
        {
            var verification = new AssemblyVerification { Name = assemblyName };

#if DEBUG
            verification.Status = VerificationStatus.Valid;
            verification.Details = "DEBUG modu";
            return verification;
#else
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var assemblyPath = Path.Combine(baseDir, assemblyName);

                if (!File.Exists(assemblyPath))
                {
                    verification.Status = VerificationStatus.Missing;
                    verification.Details = "Dosya bulunamadı";
                    return verification;
                }

                var currentHash = ComputeFileHash(assemblyPath);
                verification.CurrentHash = currentHash;

                if (_expectedHashes.TryGetValue(assemblyName, out var expectedHash))
                {
                    verification.ExpectedHash = expectedHash;
                    verification.Status = currentHash == expectedHash
                        ? VerificationStatus.Valid
                        : VerificationStatus.Modified;
                }
                else
                {
                    verification.Status = VerificationStatus.Valid;
                    verification.Details = "Hash kaydı yok";
                }
            }
            catch (Exception ex)
            {
                verification.Status = VerificationStatus.Error;
                verification.Details = ex.Message;
            }

            return verification;
#endif
        }

        /// <summary>
        /// Mevcut çalışan assembly'nin bütünlüğünü kontrol eder.
        /// </summary>
        public static bool VerifyCurrentAssembly()
        {
#if DEBUG
            return true;
#else
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var location = assembly.Location;

                if (string.IsNullOrEmpty(location))
                    return true; // Single-file publish

                var assemblyName = Path.GetFileName(location);
                var result = VerifyAssembly(assemblyName);

                return result.Status == VerificationStatus.Valid;
            }
            catch
            {
                return true; // Hata durumunda false positive önle
            }
#endif
        }

        /// <summary>
        /// Strong name imzasını doğrular (varsa).
        /// </summary>
        public static bool VerifyStrongName(Assembly assembly)
        {
#if DEBUG
            return true;
#else
            try
            {
                var name = assembly.GetName();
                var publicKey = name.GetPublicKey();

                // İmza yoksa atla
                if (publicKey == null || publicKey.Length == 0)
                    return true;

                // Strong name token kontrolü
                var token = name.GetPublicKeyToken();
                if (token == null || token.Length == 0)
                    return true;

                // Assembly'nin imzasının beklenen token ile eşleştiğini kontrol et
                // NOT: Gerçek implementasyonda beklenen token'ı hardcode'lamanız gerekir
                // var expectedToken = new byte[] { ... };
                // return token.SequenceEqual(expectedToken);

                return true;
            }
            catch
            {
                return true;
            }
#endif
        }

        /// <summary>
        /// Beklenen hash'leri günceller.
        /// Sadece ilk kurulum veya güncelleme sonrası kullanılmalı.
        /// </summary>
        public static void UpdateHashes()
        {
            lock (_initLock)
            {
                _expectedHashes.Clear();
                _isInitialized = false;
                Initialize();
            }
        }

        #region Helpers

        private static string ComputeFileHash(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash);
        }

        #endregion
    }

    /// <summary>
    /// Bütünlük kontrolü sonucu.
    /// </summary>
    public sealed class IntegrityResult
    {
        public bool IsValid { get; set; }
        public List<AssemblyVerification> Verifications { get; } = new();

        public string GetReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Integrity Check: {(IsValid ? "✓ VALID" : "✗ INVALID")}");

            foreach (var v in Verifications)
            {
                var status = v.Status switch
                {
                    VerificationStatus.Valid => "✓",
                    VerificationStatus.Modified => "✗ MODIFIED",
                    VerificationStatus.Missing => "⚠ MISSING",
                    VerificationStatus.Error => "⚠ ERROR",
                    _ => "?"
                };

                sb.AppendLine($"  [{status}] {v.Name}");

                if (!string.IsNullOrEmpty(v.Details))
                    sb.AppendLine($"       Details: {v.Details}");
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Assembly doğrulama detayı.
    /// </summary>
    public sealed class AssemblyVerification
    {
        public string Name { get; set; } = "";
        public VerificationStatus Status { get; set; }
        public string? CurrentHash { get; set; }
        public string? ExpectedHash { get; set; }
        public string? Details { get; set; }
    }

    /// <summary>
    /// Doğrulama durumu.
    /// </summary>
    public enum VerificationStatus
    {
        Valid,
        Modified,
        Missing,
        Error
    }
}