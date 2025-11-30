using System;
using System.Windows;
using System.Windows.Controls;
using UniCast.Licensing;
using UniCast.Licensing.Crypto;
using UniCast.Licensing.Hardware;
using Serilog;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;

namespace UniCast.App
{
    /// <summary>
    /// Lisans aktivasyon penceresi.
    /// DÜZELTME: AppIntegration.cs'den ayrıldı, düzgün XAML code-behind yapısı.
    /// </summary>
    public partial class ActivationWindow : Window
    {
        private bool _isActivating = false;

        public ActivationWindow()
        {
            InitializeComponent();
            LoadHardwareId();
        }

        /// <summary>
        /// Makine kimliğini yükler ve gösterir.
        /// </summary>
        private void LoadHardwareId()
        {
            try
            {
                var hwInfo = HardwareFingerprint.Validate();
                txtHardwareId.Text = hwInfo.ShortId;

                if (!hwInfo.IsValid)
                {
                    ShowStatus("⚠️ Donanım kimliği düşük güvenilirlikte. Aktivasyon sorunlu olabilir.", isError: true);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Hardware ID yükleme hatası");
                txtHardwareId.Text = "Yüklenemedi";
                ShowStatus("Donanım kimliği alınamadı.", isError: true);
            }
        }

        /// <summary>
        /// Hardware ID kopyalama butonu.
        /// </summary>
        private void BtnCopyHwId_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(txtHardwareId.Text))
                {
                    Clipboard.SetText(txtHardwareId.Text);
                    ShowStatus("✓ Makine kimliği panoya kopyalandı.", isError: false);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Clipboard kopyalama hatası");
            }
        }

        /// <summary>
        /// Lisans anahtarı değiştiğinde format kontrolü.
        /// </summary>
        private void TxtLicenseKey_TextChanged(object sender, TextChangedEventArgs e)
        {
            var key = txtLicenseKey.Text?.Trim() ?? "";

            // Otomatik tire ekleme
            if (key.Length > 0 && !key.Contains("-"))
            {
                key = FormatLicenseKey(key.Replace("-", ""));
                var caretPos = txtLicenseKey.CaretIndex;
                txtLicenseKey.Text = key;
                txtLicenseKey.CaretIndex = Math.Min(caretPos + 1, key.Length);
            }

            // Aktifleştir butonunu etkinleştir/devre dışı bırak
            btnActivate.IsEnabled = LicenseKeyFormat.Validate(key) && !_isActivating;

            // Hata mesajını temizle
            if (StatusBorder.Visibility == Visibility.Visible)
            {
                StatusBorder.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Lisans anahtarını formatla (tire ekle).
        /// </summary>
        private static string FormatLicenseKey(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            raw = raw.Replace("-", "").Replace(" ", "").ToUpperInvariant();

            var result = "";
            for (int i = 0; i < raw.Length && i < 25; i++)
            {
                if (i > 0 && i % 5 == 0)
                    result += "-";
                result += raw[i];
            }
            return result;
        }

        /// <summary>
        /// Aktivasyon butonu tıklandığında.
        /// DÜZELTME: async void yerine try-catch ile güvenli hale getirildi.
        /// </summary>
        private async void BtnActivate_Click(object sender, RoutedEventArgs e)
        {
            if (_isActivating) return;

            var licenseKey = txtLicenseKey.Text?.Trim() ?? "";

            if (string.IsNullOrEmpty(licenseKey))
            {
                ShowStatus("Lütfen lisans anahtarını girin.", isError: true);
                return;
            }

            // Format kontrolü
            if (!LicenseKeyFormat.Validate(licenseKey))
            {
                ShowStatus("Geçersiz lisans anahtarı formatı.", isError: true);
                return;
            }

            _isActivating = true;
            btnActivate.IsEnabled = false;
            progressBar.Visibility = Visibility.Visible;
            ShowStatus("Aktivasyon yapılıyor...", isError: false);

            try
            {
                var result = await LicenseManager.Instance.ActivateAsync(licenseKey);

                if (result.IsValid && result.License != null)
                {
                    Log.Information("Lisans aktivasyonu başarılı: {Type}", result.License.Type);

                    MessageBox.Show(
                        $"Aktivasyon başarılı!\n\n" +
                        $"Tür: {result.License.Type}\n" +
                        $"Kullanıcı: {result.License.LicenseeName}\n" +
                        $"Bitiş: {result.License.ExpiresAtUtc:dd.MM.yyyy}",
                        "Başarılı",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    DialogResult = true;
                    Close();
                }
                else
                {
                    Log.Warning("Lisans aktivasyonu başarısız: {Status} - {Message}", result.Status, result.Message);
                    ShowStatus($"Aktivasyon başarısız: {result.Message}", isError: true);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Aktivasyon exception");
                ShowStatus($"Bağlantı hatası: {ex.Message}", isError: true);
            }
            finally
            {
                _isActivating = false;
                btnActivate.IsEnabled = LicenseKeyFormat.Validate(txtLicenseKey.Text ?? "");
                progressBar.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Lisans satın alma sayfasını aç.
        /// </summary>
        private void BtnBuyLicense_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://unicast.app/buy",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Tarayıcı açma hatası");
                MessageBox.Show(
                    "Tarayıcı açılamadı. Lütfen manuel olarak https://unicast.app/buy adresini ziyaret edin.",
                    "Bilgi",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// Durum mesajı göster.
        /// </summary>
        private void ShowStatus(string message, bool isError)
        {
            txtStatus.Text = message;
            StatusBorder.Background = isError
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5c, 0x1a, 0x1a))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0f, 0x34, 0x60));
            StatusBorder.Visibility = Visibility.Visible;
        }
    }
}