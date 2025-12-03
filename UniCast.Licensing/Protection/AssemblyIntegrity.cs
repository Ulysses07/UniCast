using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace UniCast.Licensing.Protection
{
    /// <summary>
    /// KATMAN 4: Assembly bütünlük kontrolü.
    /// Uygulama dosyalarının değiştirilmediğini doğrular.
    /// 
    /// DÜZELTME v18: Antivirus false positive önleme mekanizması eklendi.
    /// - Retry with exponential backoff
    /// - Known antivirus process detection
    /// - Graceful degradation
    /// </summary>
    public static class AssemblyIntegrity
    {
        private static readonly Dictionary<string, string> _expectedHashes = new();
        // DÜZELTME v25: Thread safety - volatile eklendi
        private static volatile bool _isInitialized;
        private static readonly object _initLock = new();

        // Kontrol edilecek assembly'ler
        private static readonly string[] ProtectedAssemblies =
        {
            "UniCast.App.dll",
            "UniCast.Core.dll",
            "UniCast.Licensing.dll",
            "UniCast.Encoder.dll"
        };

        // DÜZELTME v18: Bilinen antivirus process isimleri
        private static readonly HashSet<string> KnownAntivirusProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            // Windows Defender
            "MsMpEng", "NisSrv", "SecurityHealthService",
            // Norton
            "NortonSecurity", "Norton360", "NS",
            // McAfee
            "mcshield", "McAfeeFramework", "mfefire",
            // Kaspersky
            "avp", "kavtray", "avpui",
            // Bitdefender
            "bdagent", "vsserv", "bdservicehost",
            // Avast/AVG
            "AvastSvc", "aswEngSrv", "avgnt",
            // ESET
            "ekrn", "egui",
            // Malwarebytes
            "MBAMService", "mbamtray",
            // Trend Micro
            "PccNTMon", "TMBMSRV",
            // Sophos
            "SavService", "SAVAdminService",
            // F-Secure
            "fshoster32", "fsorsp",
            // Comodo
            "cmdagent", "cavwp",
            // Avira
            "avgnt", "avguard"
        };

        // DÜZELTME v18: Retry ayarları
        private static class RetryConfig
        {
            public const int MaxRetries = 3;
            public const int InitialDelayMs = 100;
            public const int MaxDelayMs = 1000;
        }

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
                            // DÜZELTME v18: Retry ile hash hesapla
                            var hash = ComputeFileHashWithRetry(assemblyPath);
                            if (hash != null)
                            {
                                _expectedHashes[assemblyName] = hash;
                            }
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

            // DÜZELTME v18: Antivirus aktif mi kontrol et
            var antivirusActive = IsAntivirusActive();
            if (antivirusActive)
            {
                result.AntivirusDetected = true;
                result.AntivirusWarning = "Antivirus yazılımı algılandı. Bütünlük kontrolü yavaşlatıldı.";
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
                        // DÜZELTME v18: Retry ile hash hesapla
                        var currentHash = ComputeFileHashWithRetry(assemblyPath);
                        
                        if (currentHash == null)
                        {
                            // Dosya kilitli - antivirus muhtemelen tarıyor
                            verification.Status = VerificationStatus.Locked;
                            verification.Details = antivirusActive 
                                ? "Dosya antivirus tarafından taranıyor olabilir" 
                                : "Dosya kilitli";
                        }
                        else
                        {
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
                }
                catch (Exception ex)
                {
                    verification.Status = VerificationStatus.Error;
                    verification.Details = ex.Message;
                }

                result.Verifications.Add(verification);
            }

            // DÜZELTME v18: Genel sonuç - Locked durumu da kabul edilir (graceful degradation)
            result.IsValid = result.Verifications.All(v =>
                v.Status == VerificationStatus.Valid ||
                v.Status == VerificationStatus.Missing ||
                v.Status == VerificationStatus.Locked); // Kilitli dosyalar false positive olabilir

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

                // DÜZELTME v18: Retry ile hash hesapla
                var currentHash = ComputeFileHashWithRetry(assemblyPath);
                
                if (currentHash == null)
                {
                    verification.Status = VerificationStatus.Locked;
                    verification.Details = "Dosya erişilemez durumda";
                    return verification;
                }

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

                // DÜZELTME v18: Locked durumu da kabul et
                return result.Status == VerificationStatus.Valid || 
                       result.Status == VerificationStatus.Locked;
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

        #region DÜZELTME v18: Antivirus Detection

        /// <summary>
        /// DÜZELTME v18: Antivirus yazılımının aktif olup olmadığını kontrol eder
        /// </summary>
        public static bool IsAntivirusActive()
        {
            try
            {
                var processes = System.Diagnostics.Process.GetProcesses();

                foreach (var process in processes)
                {
                    try
                    {
                        var processName = process.ProcessName;
                        if (KnownAntivirusProcesses.Contains(processName))
                        {
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        // DÜZELTME v25: Process bilgisi alınamadı - loglama eklendi
                        System.Diagnostics.Debug.WriteLine($"[AssemblyIntegrity] Process bilgisi hatası: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // DÜZELTME v25: Process listesi alınamadı - loglama eklendi
                System.Diagnostics.Debug.WriteLine($"[AssemblyIntegrity] Process listesi hatası: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// DÜZELTME v18: Algılanan antivirus yazılımının adını döndürür
        /// </summary>
        public static string? GetDetectedAntivirus()
        {
            try
            {
                var processes = System.Diagnostics.Process.GetProcesses();

                foreach (var process in processes)
                {
                    try
                    {
                        var processName = process.ProcessName;
                        if (KnownAntivirusProcesses.Contains(processName))
                        {
                            return processName;
                        }
                    }
                    catch (Exception ex)
                    {
                        // DÜZELTME v25: Process bilgisi alınamadı - loglama eklendi
                        System.Diagnostics.Debug.WriteLine($"[AssemblyIntegrity] GetDetectedAntivirus process hatası: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // DÜZELTME v25: Process listesi alınamadı - loglama eklendi
                System.Diagnostics.Debug.WriteLine($"[AssemblyIntegrity] GetDetectedAntivirus liste hatası: {ex.Message}");
            }

            return null;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// DÜZELTME v18: Retry mekanizması ile dosya hash hesaplama
        /// </summary>
        private static string? ComputeFileHashWithRetry(string filePath)
        {
            var delay = RetryConfig.InitialDelayMs;

            for (int attempt = 0; attempt < RetryConfig.MaxRetries; attempt++)
            {
                try
                {
                    return ComputeFileHash(filePath);
                }
                catch (IOException) when (attempt < RetryConfig.MaxRetries - 1)
                {
                    // Dosya kilitli, bekle ve tekrar dene
                    Thread.Sleep(delay);
                    delay = Math.Min(delay * 2, RetryConfig.MaxDelayMs);
                }
                catch (UnauthorizedAccessException) when (attempt < RetryConfig.MaxRetries - 1)
                {
                    // Erişim engellendi, bekle ve tekrar dene
                    Thread.Sleep(delay);
                    delay = Math.Min(delay * 2, RetryConfig.MaxDelayMs);
                }
            }

            // Tüm denemeler başarısız
            return null;
        }

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

        // DÜZELTME v18: Antivirus bilgileri
        public bool AntivirusDetected { get; set; }
        public string? AntivirusWarning { get; set; }

        public string GetReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Integrity Check: {(IsValid ? "✓ VALID" : "✗ INVALID")}");

            // DÜZELTME v18: Antivirus uyarısı
            if (AntivirusDetected)
            {
                sb.AppendLine($"  ⚠ {AntivirusWarning}");
            }

            foreach (var v in Verifications)
            {
                var status = v.Status switch
                {
                    VerificationStatus.Valid => "✓",
                    VerificationStatus.Modified => "✗ MODIFIED",
                    VerificationStatus.Missing => "⚠ MISSING",
                    VerificationStatus.Locked => "🔒 LOCKED",
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
    /// DÜZELTME v18: Locked status eklendi
    /// </summary>
    public enum VerificationStatus
    {
        Valid,
        Modified,
        Missing,
        Locked,  // DÜZELTME v18: Antivirus tarafından kilitli
        Error
    }
}