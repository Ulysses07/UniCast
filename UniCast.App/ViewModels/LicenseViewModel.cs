using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Windows.Input;
using Serilog;
using UniCast.App.Infrastructure;
using UniCast.Licensing;
using UniCast.Licensing.Models;
using LicenseManager = UniCast.Licensing.LicenseManager;

namespace UniCast.App.ViewModels
{
    /// <summary>
    /// Lisans yönetimi ViewModel.
    /// LicenseView için veri bağlaması sağlar.
    /// </summary>
    public sealed class LicenseViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly LicenseManager _licenseManager;
        private LicenseInfo? _licenseInfo;
        private bool _isLoading;
        private string? _errorMessage;
        private bool _disposed;

        public LicenseViewModel()
        {
            _licenseManager = LicenseManager.Instance;

            // Komutlar
            RefreshCommand = new RelayCommand(async _ => await RefreshLicenseAsync(), _ => !IsLoading);
            DeactivateCommand = new RelayCommand(async _ => await DeactivateAsync(), _ => IsLicensed && !IsLoading);

            // Event'lere abone ol
            _licenseManager.StatusChanged += OnLicenseStatusChanged;
            _licenseManager.ValidationCompleted += OnValidationCompleted;

            // İlk yükleme
            _ = RefreshLicenseAsync();
        }

        #region Properties

        /// <summary>
        /// Geçerli lisans var mı?
        /// </summary>
        public bool IsLicensed => _licenseInfo?.Status == LicenseStatus.Valid ||
                                  _licenseInfo?.Status == LicenseStatus.GracePeriod;

        /// <summary>
        /// Trial modunda mı?
        /// </summary>
        public bool IsTrial => _licenseInfo?.Type == LicenseType.Trial;

        /// <summary>
        /// Lisans türü adı.
        /// </summary>
        public string LicenseTypeName
        {
            get
            {
                if (_licenseInfo == null)
                    return "Lisans Bulunamadı";

                return _licenseInfo.Type switch
                {
                    LicenseType.Trial => "Deneme Sürümü",
                    LicenseType.Lifetime => "Ömür Boyu Lisans",
                    _ => "Bilinmeyen"
                };
            }
        }

        /// <summary>
        /// Durum metni.
        /// </summary>
        public string StatusText
        {
            get
            {
                if (_licenseInfo == null)
                    return "Lisans durumu kontrol ediliyor...";

                return _licenseInfo.Status switch
                {
                    LicenseStatus.Valid => "Aktif",
                    LicenseStatus.GracePeriod => $"Çevrimdışı mod ({DaysRemaining} gün kaldı)",
                    LicenseStatus.Expired => "Süresi Dolmuş",
                    LicenseStatus.NotFound => "Lisans Bulunamadı",
                    LicenseStatus.InvalidSignature => "Geçersiz Lisans",
                    LicenseStatus.HardwareMismatch => "Donanım Uyuşmazlığı",
                    LicenseStatus.Revoked => "İptal Edilmiş",
                    LicenseStatus.Tampered => "Güvenlik Hatası",
                    LicenseStatus.MachineLimitExceeded => "Makine Limiti Aşıldı",
                    LicenseStatus.ServerUnreachable => "Sunucuya Ulaşılamıyor",
                    _ => "Bilinmeyen Durum"
                };
            }
        }

        /// <summary>
        /// Son kullanma tarihi metni.
        /// </summary>
        public string ExpiryText
        {
            get
            {
                if (_licenseInfo?.ExpiresAt == null)
                    return "-";

                var expires = _licenseInfo.ExpiresAt.Value;
                return expires.ToString("dd MMMM yyyy");
            }
        }

        /// <summary>
        /// Kalan gün sayısı.
        /// </summary>
        public string DaysRemaining
        {
            get
            {
                if (_licenseInfo == null)
                    return "-";

                var days = _licenseInfo.DaysRemaining;
                if (days <= 0)
                    return "Süresi doldu";
                if (days == 1)
                    return "1 gün";
                return $"{days} gün";
            }
        }

        /// <summary>
        /// Lisans sahibi.
        /// </summary>
        public string LicenseeName => _licenseInfo?.LicenseeName ?? "-";

        /// <summary>
        /// Yükleniyor mu?
        /// </summary>
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                _isLoading = value;
                OnPropertyChanged();
                (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (DeactivateCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        /// <summary>
        /// Hata mesajı.
        /// </summary>
        public string? ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                _errorMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
            }
        }

        /// <summary>
        /// Hata var mı?
        /// </summary>
        public bool HasError => !string.IsNullOrEmpty(_errorMessage);

        #endregion

        #region Commands

        public ICommand RefreshCommand { get; }
        public ICommand DeactivateCommand { get; }

        #endregion

        #region Methods

        /// <summary>
        /// Lisans bilgisini yeniler.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public async Task RefreshLicenseAsync()
        {
            if (_disposed || IsLoading)
                return;

            IsLoading = true;
            ErrorMessage = null;

            try
            {
                // Sync metodu async context'te çalıştır
                await Task.Run(() =>
                {
                    _licenseInfo = _licenseManager.GetLicenseInfo();
                });
                NotifyAllPropertiesChanged();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LicenseViewModel] Lisans bilgisi alınamadı");
                ErrorMessage = $"Lisans bilgisi alınamadı: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Lisans bilgisini yeniler (sync wrapper - LicenseView için).
        /// </summary>
        public void RefreshLicense()
        {
            _ = RefreshLicenseAsync();
        }

        /// <summary>
        /// Lisansı deaktive eder.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private async Task DeactivateAsync()
        {
            if (_disposed || IsLoading || !IsLicensed)
                return;

            IsLoading = true;
            ErrorMessage = null;

            try
            {
                var result = await _licenseManager.DeactivateAsync();

                if (result)
                {
                    Log.Information("[LicenseViewModel] Lisans deaktive edildi");
                    await RefreshLicenseAsync();
                }
                else
                {
                    ErrorMessage = "Lisans deaktive edilemedi";
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LicenseViewModel] Deaktivasyon hatası");
                ErrorMessage = $"Deaktivasyon hatası: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        #endregion

        #region Event Handlers

        private void OnLicenseStatusChanged(object? sender, LicenseStatusChangedEventArgs e)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _ = RefreshLicenseAsync();
            });
        }

        private void OnValidationCompleted(object? sender, LicenseValidationResult e)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                NotifyAllPropertiesChanged();
            });
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void NotifyAllPropertiesChanged()
        {
            OnPropertyChanged(nameof(IsLicensed));
            OnPropertyChanged(nameof(IsTrial));
            OnPropertyChanged(nameof(LicenseTypeName));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ExpiryText));
            OnPropertyChanged(nameof(DaysRemaining));
            OnPropertyChanged(nameof(LicenseeName));
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // Event'lerden çık
            _licenseManager.StatusChanged -= OnLicenseStatusChanged;
            _licenseManager.ValidationCompleted -= OnValidationCompleted;

            PropertyChanged = null;
        }

        #endregion
    }
}