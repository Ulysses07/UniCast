using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using UniCast.Licensing.Crypto;
using UniCast.Licensing.Hardware;
using UniCast.Licensing.Models;
using UniCast.Licensing.Protection;

namespace UniCast.Licensing
{
    /// <summary>
    /// Merkezi lisans yönetim sınıfı.
    /// Tüm koruma katmanlarını koordine eder.
    /// Thread-safe, proper dispose pattern.
    /// </summary>
    public sealed class LicenseManager : ILicenseManager
    {
        private static readonly Lazy<LicenseManager> _instance = new(() => new LicenseManager(), LazyThreadSafetyMode.ExecutionAndPublication);
        public static LicenseManager Instance => _instance.Value;

        private readonly string _licenseFilePath;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _lock = new(1, 1);

        private LicenseData? _currentLicense;
        private LicenseStatus _currentStatus = LicenseStatus.NotFound;
        private Timer? _validationTimer;
        private bool _disposed;

        // Event handler referansları (memory leak önleme)
        private EventHandler<ThreatDetectedEventArgs>? _threatHandler;

        // Olaylar
        public event EventHandler<LicenseStatusChangedEventArgs>? StatusChanged;
        public event EventHandler<LicenseValidationResult>? ValidationCompleted;

        // Ayarlar
        public string LicenseServerUrl { get; set; } = "https://license.unicastapp.com/api/v1";
        public int OnlineValidationIntervalHours { get; set; } = 24;
        public bool AllowOfflineMode { get; set; } = true;
        public int MaxRetryAttempts { get; set; } = 3;
        public int RetryDelayMs { get; set; } = 1000;

        private LicenseManager()
        {
            // Lisans dosyası konumu
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var licenseDir = Path.Combine(appData, "UniCast", "License");

            try
            {
                Directory.CreateDirectory(licenseDir);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LicenseManager] Lisans dizini oluşturulamadı");
            }

            _licenseFilePath = Path.Combine(licenseDir, "license.dat");

            // HTTP client with retry handler
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 5,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };

            _httpClient = new HttpClient(handler)
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
            ThrowIfDisposed();

            try
            {
#if DEBUG
                Log.Warning("[LicenseManager] DEBUG modu: Güvenlik kontrolleri atlanıyor");
#else
                // 1. Runtime korumasını başlat
                RuntimeProtection.Initialize();
                _threatHandler = OnThreatDetected;
                RuntimeProtection.ThreatDetected += _threatHandler;

                // 2. Assembly bütünlüğünü kontrol et
                AssemblyIntegrity.Initialize();
                var integrityResult = AssemblyIntegrity.VerifyAll();
                if (!integrityResult.IsValid)
                {
                    Log.Warning("[LicenseManager] Assembly bütünlük kontrolü başarısız: {Report}", integrityResult.GetReport());
                    return LicenseValidationResult.Failure(
                        LicenseStatus.Tampered,
                        "Uygulama dosyaları değiştirilmiş",
                        integrityResult.GetReport());
                }

                // 3. Güvenlik kontrolü
                var securityResult = RuntimeProtection.PerformSecurityChecks();
                if (!securityResult.IsSecure)
                {
                    Log.Warning("[LicenseManager] Güvenlik kontrolü başarısız: {Report}", securityResult.GetReport());
                    return LicenseValidationResult.Failure(
                        LicenseStatus.Tampered,
                        "Güvenlik tehdidi tespit edildi",
                        securityResult.GetReport());
                }
#endif

                // 4. Mevcut lisansı doğrula
                var result = await ValidateCurrentLicenseAsync();

                // 5. Periyodik doğrulama zamanlayıcısı
                StartValidationTimer();

                ValidationCompleted?.Invoke(this, result);
                return result;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LicenseManager] InitializeAsync hatası");

#if DEBUG
                Log.Warning("[LicenseManager] DEBUG: Hata yutuldu, trial başlatılıyor");
                return StartTrial();
#else
                return LicenseValidationResult.Failure(
                    LicenseStatus.Tampered,
                    $"Lisans başlatma hatası: {ex.Message}");
#endif
            }
        }

        /// <summary>
        /// Lisans anahtarı ile aktivasyon yapar.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public async Task<LicenseValidationResult> ActivateAsync(string licenseKey)
        {
            ThrowIfDisposed();

            await _lock.WaitAsync();
            try
            {
                // Format kontrolü
                if (!LicenseKeyFormat.Validate(licenseKey))
                {
                    Log.Warning("[LicenseManager] Geçersiz lisans key formatı");
                    return LicenseValidationResult.Failure(
                        LicenseStatus.InvalidSignature,
                        "Geçersiz lisans anahtarı formatı");
                }

                // Hardware bilgisi
                var hwInfo = HardwareFingerprint.Validate();
                if (!hwInfo.IsValid)
                {
                    Log.Warning("[LicenseManager] Hardware ID oluşturulamadı. Score: {Score}", hwInfo.Score);
                    return LicenseValidationResult.Failure(
                        LicenseStatus.HardwareMismatch,
                        "Donanım kimliği oluşturulamadı",
                        hwInfo.GetDiagnosticsReport());
                }

                // Sunucuya aktivasyon isteği (retry mekanizması ile)
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

                var response = await SendWithRetryAsync(
                    () => SendActivationRequestAsync(request),
                    "Aktivasyon");

                if (response.Success && response.License != null)
                {
                    // İmza doğrulama
                    if (!LicenseSigner.Verify(response.License))
                    {
                        Log.Warning("[LicenseManager] Lisans imza doğrulaması başarısız");
                        return LicenseValidationResult.Failure(
                            LicenseStatus.InvalidSignature,
                            "Lisans imzası doğrulanamadı");
                    }

                    // Lisansı kaydet
                    _currentLicense = response.License;
                    _currentStatus = LicenseStatus.Valid;
                    LicenseEncryption.SaveEncrypted(_currentLicense, _licenseFilePath);

                    Log.Information("[LicenseManager] Aktivasyon başarılı. Tür: {Type}", _currentLicense.Type);

                    var result = LicenseValidationResult.Success(_currentLicense);
                    RaiseStatusChanged(LicenseStatus.Valid);
                    return result;
                }

                Log.Warning("[LicenseManager] Aktivasyon başarısız: {Message}", response.Message);
                return LicenseValidationResult.Failure(
                    LicenseStatus.NotFound,
                    response.Message ?? "Aktivasyon başarısız");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LicenseManager] ActivateAsync hatası");
                return LicenseValidationResult.Failure(
                    LicenseStatus.ServerUnreachable,
                    $"Aktivasyon hatası: {ex.Message}");
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Lisans anahtarı ile aktivasyon yapar (senkron wrapper).
        /// </summary>
        [SupportedOSPlatform("windows")]
        public ActivationResult ActivateLicense(string licenseKey)
        {
            try
            {
                var result = ActivateAsync(licenseKey).GetAwaiter().GetResult();
                return new ActivationResult
                {
                    Success = result.IsValid,
                    ErrorMessage = result.IsValid ? null : result.Message
                };
            }
            catch (Exception ex)
            {
                return new ActivationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// Mevcut lisansı devre dışı bırakır (makine değişikliği için).
        /// </summary>
        [SupportedOSPlatform("windows")]
        public async Task<bool> DeactivateAsync()
        {
            ThrowIfDisposed();

            await _lock.WaitAsync();
            try
            {
                if (_currentLicense == null)
                {
                    Log.Warning("[LicenseManager] Deaktivasyon: Mevcut lisans yok");
                    return false;
                }

                var hwInfo = HardwareFingerprint.Validate();

                // Sunucuya deaktivasyon isteği
                var request = new DeactivationRequest
                {
                    LicenseId = _currentLicense.LicenseId,
                    LicenseKey = _currentLicense.LicenseKey,
                    HardwareId = hwInfo.HardwareId
                };

                var success = await SendWithRetryAsync(
                    () => SendDeactivationRequestAsync(request),
                    "Deaktivasyon");

                if (success)
                {
                    // Yerel lisansı güvenli sil
                    LicenseEncryption.SecureDelete(_licenseFilePath);

                    _currentLicense = null;
                    _currentStatus = LicenseStatus.NotFound;

                    Log.Information("[LicenseManager] Deaktivasyon başarılı");
                    RaiseStatusChanged(LicenseStatus.NotFound);
                }

                return success;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LicenseManager] DeactivateAsync hatası");
                return false;
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
            ThrowIfDisposed();
            return await ValidateCurrentLicenseAsync();
        }

        /// <summary>
        /// Lisansın geçerli olup olmadığını kontrol eder.
        /// Trial veya Lifetime fark etmez, geçerliyse tüm özellikler açık.
        /// </summary>
        public bool IsLicenseValid()
        {
            if (_disposed)
                return false;

            if (_currentLicense == null)
                return false;

            // Trial için süre kontrolü
            if (_currentLicense.IsTrial && _currentLicense.IsExpired)
                return false;

            return _currentStatus == LicenseStatus.Valid ||
                   _currentStatus == LicenseStatus.GracePeriod ||
                   _currentStatus == LicenseStatus.SupportExpired;
        }

        /// <summary>
        /// Bakım/destek süresinin aktif olup olmadığını kontrol eder.
        /// </summary>
        public bool IsSupportActive()
        {
            if (_disposed || _currentLicense == null)
                return false;

            return _currentLicense.IsSupportActive;
        }

        /// <summary>
        /// Mevcut lisans bilgilerini döndürür.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public LicenseInfo GetLicenseInfo()
        {
            string hardwareId;
            try
            {
                hardwareId = HardwareFingerprint.GenerateShort();
            }
            catch
            {
                hardwareId = "N/A";
            }

            return new LicenseInfo
            {
                Status = _currentStatus,
                Type = _currentLicense?.Type ?? LicenseType.Trial,
                LicenseeName = _currentLicense?.LicenseeName ?? "",
                ExpiresAt = _currentLicense?.ExpiresAtUtc,
                DaysRemaining = _currentLicense?.DaysRemaining ?? 0,
                IsSupportActive = _currentLicense?.IsSupportActive ?? false,
                SupportDaysRemaining = _currentLicense?.SupportDaysRemaining ?? 0,
                SupportExpiresAt = _currentLicense?.SupportExpiryUtc,
                HardwareId = hardwareId
            };
        }

        /// <summary>
        /// Trial lisansı başlatır.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public LicenseValidationResult StartTrial()
        {
            ThrowIfDisposed();

            try
            {
                var hwInfo = HardwareFingerprint.Validate();
                if (!hwInfo.IsValid)
                {
                    Log.Warning("[LicenseManager] Trial: Hardware ID oluşturulamadı");
                    return LicenseValidationResult.Failure(
                        LicenseStatus.HardwareMismatch,
                        "Donanım kimliği oluşturulamadı");
                }

                _currentLicense = new LicenseData
                {
                    LicenseId = Guid.NewGuid().ToString("N"),
                    LicenseKey = "TRIAL-" + hwInfo.ShortId,
                    Type = LicenseType.Trial,
                    LicenseeName = "Trial User",
                    LicenseeEmail = "",
                    IssuedAtUtc = DateTime.UtcNow,
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(14),
                    SupportExpiryUtc = DateTime.UtcNow.AddDays(14), // Trial için destek de 14 gün
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
                    ],
                    Signature = "TRIAL_LOCAL"
                };

                _currentStatus = LicenseStatus.Valid;
                LicenseEncryption.SaveEncrypted(_currentLicense, _licenseFilePath);

                Log.Information("[LicenseManager] Trial lisansı başlatıldı. Süre: 14 gün");
                RaiseStatusChanged(LicenseStatus.Valid);

                return LicenseValidationResult.Success(_currentLicense);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LicenseManager] StartTrial hatası");
                return LicenseValidationResult.Failure(
                    LicenseStatus.NotFound,
                    $"Trial başlatma hatası: {ex.Message}");
            }
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
                    Log.Debug("[LicenseManager] Lisans dosyası bulunamadı");
                    return LicenseValidationResult.Failure(
                        LicenseStatus.NotFound,
                        "Lisans bulunamadı");
                }

                // 2. Süre kontrolü
                if (_currentLicense.IsExpired)
                {
                    _currentStatus = LicenseStatus.Expired;
                    Log.Warning("[LicenseManager] Lisans süresi dolmuş");
                    RaiseStatusChanged(LicenseStatus.Expired);
                    return LicenseValidationResult.Failure(
                        LicenseStatus.Expired,
                        "Lisans süresi dolmuş");
                }

                // 3. İmza kontrolü (Trial hariç)
                if (!_currentLicense.IsTrial && !LicenseSigner.Verify(_currentLicense))
                {
                    _currentStatus = LicenseStatus.InvalidSignature;
                    Log.Warning("[LicenseManager] Lisans imzası geçersiz");
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
                    Log.Warning("[LicenseManager] Hardware uyuşmazlığı");
                    RaiseStatusChanged(LicenseStatus.HardwareMismatch);
                    return LicenseValidationResult.Failure(
                        LicenseStatus.HardwareMismatch,
                        "Bu lisans farklı bir bilgisayarda kullanılıyor");
                }

                // 5. Online doğrulama (isteğe bağlı)
                var timeSinceLastValidation = DateTime.UtcNow - _currentLicense.LastValidationUtc;
                var shouldValidateOnline = timeSinceLastValidation.TotalHours >= OnlineValidationIntervalHours;

                if (shouldValidateOnline && !_currentLicense.IsTrial)
                {
                    var onlineResult = await ValidateOnlineAsync();

                    if (!onlineResult.IsValid)
                    {
                        // Grace period kontrolü
                        if (AllowOfflineMode && timeSinceLastValidation.TotalDays < _currentLicense.OfflineGraceDays)
                        {
                            var graceDaysRemaining = _currentLicense.OfflineGraceDays - (int)timeSinceLastValidation.TotalDays;
                            _currentStatus = LicenseStatus.GracePeriod;
                            Log.Information("[LicenseManager] Grace period aktif. Kalan gün: {Days}", graceDaysRemaining);
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
                Log.Debug("[LicenseManager] Lisans doğrulama başarılı");
                return LicenseValidationResult.Success(_currentLicense);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LicenseManager] ValidateCurrentLicenseAsync hatası");

#if DEBUG
                return LicenseValidationResult.Failure(LicenseStatus.NotFound, "Lisans bulunamadı");
#else
                return LicenseValidationResult.Failure(
                    LicenseStatus.Tampered,
                    $"Doğrulama hatası: {ex.Message}");
#endif
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
                    Log.Warning("[LicenseManager] Online doğrulama HTTP hatası: {StatusCode}", response.StatusCode);
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
            catch (HttpRequestException ex)
            {
                Log.Warning(ex, "[LicenseManager] Online doğrulama ağ hatası");
                return LicenseValidationResult.Failure(
                    LicenseStatus.ServerUnreachable,
                    "Lisans sunucusuna ulaşılamadı");
            }
            catch (TaskCanceledException ex)
            {
                Log.Warning(ex, "[LicenseManager] Online doğrulama timeout");
                return LicenseValidationResult.Failure(
                    LicenseStatus.ServerUnreachable,
                    "Sunucu yanıt vermedi");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LicenseManager] Online doğrulama hatası");
                return LicenseValidationResult.Failure(
                    LicenseStatus.ServerUnreachable,
                    $"Bağlantı hatası: {ex.Message}");
            }
        }

        #endregion

        #region Server Communication with Retry

        private async Task<T> SendWithRetryAsync<T>(Func<Task<T>> operation, string operationName)
        {
            Exception? lastException = null;

            for (int attempt = 0; attempt < MaxRetryAttempts; attempt++)
            {
                try
                {
                    return await operation();
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    Log.Warning("[LicenseManager] {Operation} deneme {Attempt} başarısız: {Message}",
                        operationName, attempt + 1, ex.Message);
                }
                catch (TaskCanceledException ex)
                {
                    lastException = ex;
                    Log.Warning("[LicenseManager] {Operation} timeout deneme {Attempt}",
                        operationName, attempt + 1);
                }

                if (attempt < MaxRetryAttempts - 1)
                {
                    var delay = RetryDelayMs * (int)Math.Pow(2, attempt); // Exponential backoff
                    await Task.Delay(delay).ConfigureAwait(false);
                }
            }

            throw lastException ?? new Exception($"{operationName} başarısız");
        }

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

                var errorContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return new ActivationResponse
                {
                    Success = false,
                    Message = $"Sunucu hatası: {response.StatusCode} - {errorContent}"
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
            catch (Exception ex)
            {
                Log.Error(ex, "[LicenseManager] Deaktivasyon isteği hatası");
                return false;
            }
        }

        #endregion

        #region Helpers

        private void StartValidationTimer()
        {
            _validationTimer?.Dispose();

            var interval = TimeSpan.FromHours(OnlineValidationIntervalHours);

            _validationTimer = new Timer(
                async _ =>
                {
                    try
                    {
                        await ValidateCurrentLicenseAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[LicenseManager] Periyodik doğrulama hatası");
                    }
                },
                null,
                interval,
                interval);
        }

        private void OnThreatDetected(object? sender, ThreatDetectedEventArgs e)
        {
            Log.Warning("[LicenseManager] Tehdit tespit edildi: {Type} - {Message}", e.Type, e.Message);

            _currentStatus = LicenseStatus.Tampered;
            RaiseStatusChanged(LicenseStatus.Tampered);
        }

        private void RaiseStatusChanged(LicenseStatus newStatus)
        {
            try
            {
                StatusChanged?.Invoke(this, new LicenseStatusChangedEventArgs(newStatus));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LicenseManager] StatusChanged event hatası");
            }
        }

        private static string GetAppVersion()
        {
            try
            {
                return System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";
            }
            catch
            {
                return "1.0.0";
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(LicenseManager));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // Event handler'ları temizle
            if (_threatHandler != null)
            {
                RuntimeProtection.ThreatDetected -= _threatHandler;
                _threatHandler = null;
            }

            // Timer'ı durdur
            _validationTimer?.Dispose();
            _validationTimer = null;

            // HTTP client'ı dispose et
            _httpClient.Dispose();

            // Lock'u dispose et
            _lock.Dispose();

            // Runtime protection'ı kapat
            RuntimeProtection.Shutdown();

            // Event'leri temizle
            StatusChanged = null;
            ValidationCompleted = null;

            Log.Debug("[LicenseManager] Disposed");
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
        public bool IsSupportActive { get; set; }
        public int SupportDaysRemaining { get; set; }
        public DateTime? SupportExpiresAt { get; set; }
        public string HardwareId { get; set; } = "";
    }

    public sealed class LicenseStatusChangedEventArgs : EventArgs
    {
        public LicenseStatus NewStatus { get; }
        public DateTime Timestamp { get; }

        public LicenseStatusChangedEventArgs(LicenseStatus status)
        {
            NewStatus = status;
            Timestamp = DateTime.UtcNow;
        }
    }

    public sealed class ActivationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }

    #endregion
}