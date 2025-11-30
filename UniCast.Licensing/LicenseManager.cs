using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Licensing.Crypto;
using UniCast.Licensing.Hardware;
using UniCast.Licensing.Models;
using UniCast.Licensing.Protection;

namespace UniCast.Licensing
{
    /// <summary>
    /// Merkezi lisans yönetim sınıfı.
    /// Tüm koruma katmanlarını koordine eder.
    /// </summary>
    public sealed class LicenseManager : IDisposable
    {
        private static readonly Lazy<LicenseManager> _instance = new(() => new LicenseManager());
        public static LicenseManager Instance => _instance.Value;

        private readonly string _licenseFilePath;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _lock = new(1, 1);

        private LicenseData? _currentLicense;
        private LicenseStatus _currentStatus = LicenseStatus.NotFound;
        private Timer? _validationTimer;
        private bool _disposed;

        // Olaylar
        public event EventHandler<LicenseStatusChangedEventArgs>? StatusChanged;
        public event EventHandler<LicenseValidationResult>? ValidationCompleted;

        // Ayarlar
        public string LicenseServerUrl { get; set; } = "https://license.unicast.app/api/v1";
        public int OnlineValidationIntervalHours { get; set; } = 24;
        public bool AllowOfflineMode { get; set; } = true;

        private LicenseManager()
        {
            // Lisans dosyası konumu
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var licenseDir = Path.Combine(appData, "UniCast", "License");
            Directory.CreateDirectory(licenseDir);
            _licenseFilePath = Path.Combine(licenseDir, "license.dat");

            // HTTP client
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "UniCast-License-Client/1.0");
        }

        #region Public API

        /// <summary>
        /// Lisans sistemini başlatır ve mevcut lisansı doğrular.
        /// Uygulama başlangıcında çağrılmalı.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public async Task<LicenseValidationResult> InitializeAsync()
        {
            // 1. Runtime korumasını başlat
            RuntimeProtection.Initialize();
            RuntimeProtection.ThreatDetected += OnThreatDetected;

            // 2. Assembly bütünlüğünü kontrol et
            AssemblyIntegrity.Initialize();
            var integrityResult = AssemblyIntegrity.VerifyAll();
            if (!integrityResult.IsValid)
            {
                return LicenseValidationResult.Failure(
                    LicenseStatus.Tampered,
                    "Uygulama dosyaları değiştirilmiş",
                    integrityResult.GetReport());
            }

            // 3. Güvenlik kontrolü
            var securityResult = RuntimeProtection.PerformSecurityChecks();
            if (!securityResult.IsSecure)
            {
                return LicenseValidationResult.Failure(
                    LicenseStatus.Tampered,
                    "Güvenlik tehdidi tespit edildi",
                    securityResult.GetReport());
            }

            // 4. Mevcut lisansı doğrula
            var result = await ValidateCurrentLicenseAsync();

            // 5. Periyodik doğrulama zamanlayıcısı
            StartValidationTimer();

            return result;
        }

        /// <summary>
        /// Lisans anahtarı ile aktivasyon yapar.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public async Task<LicenseValidationResult> ActivateAsync(string licenseKey)
        {
            await _lock.WaitAsync();
            try
            {
                // Format kontrolü
                if (!LicenseKeyFormat.Validate(licenseKey))
                {
                    return LicenseValidationResult.Failure(
                        LicenseStatus.InvalidSignature,
                        "Geçersiz lisans anahtarı formatı");
                }

                // Hardware bilgisi
                var hwInfo = HardwareFingerprint.Validate();
                if (!hwInfo.IsValid)
                {
                    return LicenseValidationResult.Failure(
                        LicenseStatus.HardwareMismatch,
                        "Donanım kimliği oluşturulamadı",
                        hwInfo.GetDiagnosticsReport());
                }

                // Sunucuya aktivasyon isteği
                var request = new ActivationRequest
                {
                    LicenseKey = LicenseKeyFormat.Normalize(licenseKey),
                    HardwareId = hwInfo.HardwareId,
                    HardwareIdShort = hwInfo.ShortId,
                    MachineName = Environment.MachineName,
                    ComponentsHash = hwInfo.ComponentsRaw,
                    OsVersion = Environment.OSVersion.ToString(),
                    AppVersion = GetAppVersion()
                };

                var response = await SendActivationRequestAsync(request);

                if (response.Success && response.License != null)
                {
                    // İmza doğrulama
                    if (!LicenseSigner.Verify(response.License))
                    {
                        return LicenseValidationResult.Failure(
                            LicenseStatus.InvalidSignature,
                            "Lisans imzası doğrulanamadı");
                    }

                    // Lisansı kaydet
                    _currentLicense = response.License;
                    _currentStatus = LicenseStatus.Valid;
                    LicenseEncryption.SaveEncrypted(_currentLicense, _licenseFilePath);

                    var result = LicenseValidationResult.Success(_currentLicense);
                    RaiseStatusChanged(LicenseStatus.Valid);
                    return result;
                }

                return LicenseValidationResult.Failure(
                    LicenseStatus.NotFound,
                    response.Message ?? "Aktivasyon başarısız");
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Mevcut lisansı devre dışı bırakır (makine değişikliği için).
        /// </summary>
        [SupportedOSPlatform("windows")]
        public async Task<bool> DeactivateAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (_currentLicense == null)
                    return false;

                var hwInfo = HardwareFingerprint.Validate();

                // Sunucuya deaktivasyon isteği
                var request = new DeactivationRequest
                {
                    LicenseId = _currentLicense.LicenseId,
                    LicenseKey = _currentLicense.LicenseKey,
                    HardwareId = hwInfo.HardwareId
                };

                var success = await SendDeactivationRequestAsync(request);

                if (success)
                {
                    // Yerel lisansı sil
                    if (File.Exists(_licenseFilePath))
                        File.Delete(_licenseFilePath);

                    _currentLicense = null;
                    _currentStatus = LicenseStatus.NotFound;
                    RaiseStatusChanged(LicenseStatus.NotFound);
                }

                return success;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Mevcut lisansı doğrular.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public async Task<LicenseValidationResult> ValidateAsync()
        {
            return await ValidateCurrentLicenseAsync();
        }

        /// <summary>
        /// Belirli bir özelliğin kullanılabilir olup olmadığını kontrol eder.
        /// </summary>
        public bool HasFeature(LicenseFeatures feature)
        {
            if (_currentLicense == null || _currentStatus != LicenseStatus.Valid)
                return false;

            return _currentLicense.HasFeature(feature);
        }

        /// <summary>
        /// Mevcut lisans bilgilerini döndürür.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public LicenseInfo GetLicenseInfo()
        {
            return new LicenseInfo
            {
                Status = _currentStatus,
                Type = _currentLicense?.Type ?? LicenseType.Trial,
                LicenseeName = _currentLicense?.LicenseeName ?? "",
                ExpiresAt = _currentLicense?.ExpiresAtUtc,
                DaysRemaining = _currentLicense?.DaysRemaining ?? 0,
                Features = _currentLicense?.Features ?? LicenseFeatures.None,
                HardwareId = HardwareFingerprint.GenerateShort()
            };
        }

        /// <summary>
        /// Trial lisansı başlatır.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public LicenseValidationResult StartTrial()
        {
            var hwInfo = HardwareFingerprint.Validate();
            if (!hwInfo.IsValid)
            {
                return LicenseValidationResult.Failure(
                    LicenseStatus.HardwareMismatch,
                    "Donanım kimliği oluşturulamadı");
            }

            _currentLicense = new LicenseData
            {
                LicenseId = Guid.NewGuid().ToString("N"),
                LicenseKey = "TRIAL-" + hwInfo.ShortId,
                Type = LicenseType.Trial,
                Features = LicenseFeatures.TrialFeatures,
                LicenseeName = "Trial User",
                LicenseeEmail = "",
                IssuedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(14),
                MaxMachines = 1,
                Activations =
                [
                    new HardwareActivation
                    {
                        HardwareId = hwInfo.HardwareId,
                        HardwareIdShort = hwInfo.ShortId,
                        MachineName = Environment.MachineName,
                        ActivatedAtUtc = DateTime.UtcNow,
                        ComponentsHash = hwInfo.ComponentsRaw
                    }
                ]
            };

            // Trial'ı imzala (lokal)
            _currentLicense.Signature = "TRIAL_LOCAL";

            _currentStatus = LicenseStatus.Valid;
            LicenseEncryption.SaveEncrypted(_currentLicense, _licenseFilePath);

            RaiseStatusChanged(LicenseStatus.Valid);
            return LicenseValidationResult.Success(_currentLicense);
        }

        #endregion

        #region Internal Validation

        [SupportedOSPlatform("windows")]
        private async Task<LicenseValidationResult> ValidateCurrentLicenseAsync()
        {
            await _lock.WaitAsync();
            try
            {
                // 1. Lisans dosyasını yükle
                _currentLicense = LicenseEncryption.LoadEncrypted(_licenseFilePath);

                if (_currentLicense == null)
                {
                    _currentStatus = LicenseStatus.NotFound;
                    return LicenseValidationResult.Failure(
                        LicenseStatus.NotFound,
                        "Lisans bulunamadı");
                }

                // 2. Süre kontrolü
                if (_currentLicense.IsExpired)
                {
                    _currentStatus = LicenseStatus.Expired;
                    RaiseStatusChanged(LicenseStatus.Expired);
                    return LicenseValidationResult.Failure(
                        LicenseStatus.Expired,
                        "Lisans süresi dolmuş");
                }

                // 3. İmza kontrolü (Trial hariç)
                if (!_currentLicense.IsTrial && !LicenseSigner.Verify(_currentLicense))
                {
                    _currentStatus = LicenseStatus.InvalidSignature;
                    RaiseStatusChanged(LicenseStatus.InvalidSignature);
                    return LicenseValidationResult.Failure(
                        LicenseStatus.InvalidSignature,
                        "Lisans imzası geçersiz");
                }

                // 4. Hardware kontrolü
                var hwInfo = HardwareFingerprint.Validate();
                var activation = _currentLicense.Activations.Find(a =>
                    a.HardwareId == hwInfo.HardwareId ||
                    HardwareFingerprint.CalculateSimilarity(a.ComponentsHash, hwInfo.ComponentsRaw) >= 60);

                if (activation == null)
                {
                    _currentStatus = LicenseStatus.HardwareMismatch;
                    RaiseStatusChanged(LicenseStatus.HardwareMismatch);
                    return LicenseValidationResult.Failure(
                        LicenseStatus.HardwareMismatch,
                        "Bu lisans farklı bir bilgisayarda kullanılıyor");
                }

                // 5. Online doğrulama (isteğe bağlı)
                var timeSinceLastValidation = DateTime.UtcNow - _currentLicense.LastValidationUtc;
                var shouldValidateOnline = timeSinceLastValidation.TotalHours >= OnlineValidationIntervalHours;

                if (shouldValidateOnline)
                {
                    var onlineResult = await ValidateOnlineAsync();

                    if (!onlineResult.IsValid)
                    {
                        // Grace period kontrolü
                        if (AllowOfflineMode && timeSinceLastValidation.TotalDays < _currentLicense.OfflineGraceDays)
                        {
                            var graceDaysRemaining = _currentLicense.OfflineGraceDays - (int)timeSinceLastValidation.TotalDays;
                            _currentStatus = LicenseStatus.GracePeriod;
                            return LicenseValidationResult.Grace(_currentLicense, graceDaysRemaining);
                        }

                        _currentStatus = onlineResult.Status;
                        RaiseStatusChanged(onlineResult.Status);
                        return onlineResult;
                    }

                    // Başarılı online doğrulama - zamanı güncelle
                    _currentLicense.LastValidationUtc = DateTime.UtcNow;
                    LicenseEncryption.SaveEncrypted(_currentLicense, _licenseFilePath);
                }

                _currentStatus = LicenseStatus.Valid;
                return LicenseValidationResult.Success(_currentLicense);
            }
            finally
            {
                _lock.Release();
            }
        }

        [SupportedOSPlatform("windows")]
        private async Task<LicenseValidationResult> ValidateOnlineAsync()
        {
            try
            {
                if (_currentLicense == null)
                    return LicenseValidationResult.Failure(LicenseStatus.NotFound, "Lisans yok");

                var hwInfo = HardwareFingerprint.Validate();

                var request = new OnlineValidationRequest
                {
                    LicenseId = _currentLicense.LicenseId,
                    LicenseKey = _currentLicense.LicenseKey,
                    HardwareId = hwInfo.HardwareId,
                    AppVersion = GetAppVersion()
                };

                var response = await _httpClient.PostAsJsonAsync(
                    $"{LicenseServerUrl}/validate",
                    request);

                if (!response.IsSuccessStatusCode)
                {
                    return LicenseValidationResult.Failure(
                        LicenseStatus.ServerUnreachable,
                        "Lisans sunucusuna ulaşılamadı");
                }

                var result = await response.Content.ReadFromJsonAsync<OnlineValidationResponse>();

                if (result == null || !result.Valid)
                {
                    return LicenseValidationResult.Failure(
                        result?.Status ?? LicenseStatus.Revoked,
                        result?.Message ?? "Online doğrulama başarısız");
                }

                return LicenseValidationResult.Success(_currentLicense);
            }
            catch (HttpRequestException)
            {
                return LicenseValidationResult.Failure(
                    LicenseStatus.ServerUnreachable,
                    "Lisans sunucusuna ulaşılamadı");
            }
            catch (Exception ex)
            {
                return LicenseValidationResult.Failure(
                    LicenseStatus.ServerUnreachable,
                    $"Bağlantı hatası: {ex.Message}");
            }
        }

        #endregion

        #region Server Communication

        private async Task<ActivationResponse> SendActivationRequestAsync(ActivationRequest request)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync(
                    $"{LicenseServerUrl}/activate",
                    request);

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<ActivationResponse>()
                           ?? new ActivationResponse { Success = false, Message = "Geçersiz yanıt" };
                }

                return new ActivationResponse
                {
                    Success = false,
                    Message = $"Sunucu hatası: {response.StatusCode}"
                };
            }
            catch (Exception ex)
            {
                return new ActivationResponse
                {
                    Success = false,
                    Message = $"Bağlantı hatası: {ex.Message}"
                };
            }
        }

        private async Task<bool> SendDeactivationRequestAsync(DeactivationRequest request)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync(
                    $"{LicenseServerUrl}/deactivate",
                    request);

                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Helpers

        [SupportedOSPlatform("windows")]
        private void StartValidationTimer()
        {
            _validationTimer?.Dispose();
            _validationTimer = new Timer(
                async _ => await ValidateCurrentLicenseAsync(),
                null,
                TimeSpan.FromHours(OnlineValidationIntervalHours),
                TimeSpan.FromHours(OnlineValidationIntervalHours));
        }

        private void OnThreatDetected(object? sender, ThreatDetectedEventArgs e)
        {
            // Tehdit tespit edildi - lisansı geçersiz say
            _currentStatus = LicenseStatus.Tampered;
            RaiseStatusChanged(LicenseStatus.Tampered);

            // Uygulamayı kapat (isteğe bağlı)
            // Environment.Exit(1);
        }

        private void RaiseStatusChanged(LicenseStatus newStatus)
        {
            StatusChanged?.Invoke(this, new LicenseStatusChangedEventArgs(newStatus));
        }

        private static string GetAppVersion()
        {
            return System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _validationTimer?.Dispose();
            _httpClient.Dispose();
            _lock.Dispose();
            RuntimeProtection.Shutdown();

            _disposed = true;
        }

        #endregion
    }

    #region DTOs

    public sealed class ActivationRequest
    {
        public string LicenseKey { get; set; } = "";
        public string HardwareId { get; set; } = "";
        public string HardwareIdShort { get; set; } = "";
        public string MachineName { get; set; } = "";
        public string ComponentsHash { get; set; } = "";
        public string OsVersion { get; set; } = "";
        public string AppVersion { get; set; } = "";
    }

    public sealed class ActivationResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public LicenseData? License { get; set; }
    }

    public sealed class DeactivationRequest
    {
        public string LicenseId { get; set; } = "";
        public string LicenseKey { get; set; } = "";
        public string HardwareId { get; set; } = "";
    }

    public sealed class OnlineValidationRequest
    {
        public string LicenseId { get; set; } = "";
        public string LicenseKey { get; set; } = "";
        public string HardwareId { get; set; } = "";
        public string AppVersion { get; set; } = "";
    }

    public sealed class OnlineValidationResponse
    {
        public bool Valid { get; set; }
        public LicenseStatus Status { get; set; }
        public string? Message { get; set; }
    }

    public sealed class LicenseInfo
    {
        public LicenseStatus Status { get; set; }
        public LicenseType Type { get; set; }
        public string LicenseeName { get; set; } = "";
        public DateTime? ExpiresAt { get; set; }
        public int DaysRemaining { get; set; }
        public LicenseFeatures Features { get; set; }
        public string HardwareId { get; set; } = "";
    }

    public sealed class LicenseStatusChangedEventArgs : EventArgs
    {
        public LicenseStatus NewStatus { get; }
        public LicenseStatusChangedEventArgs(LicenseStatus status) => NewStatus = status;
    }

    #endregion
}