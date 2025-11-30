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
    /// </summary>
    public partial class ActivationWindow : Window
    {
        private bool _isActivating = false;

        public ActivationWindow()
        {
            InitializeComponent();
            LoadHardwareId();
        }

        private void LoadHardwareId()
        {
            try
            {
                var hwInfo = HardwareFingerprint.Validate();
                txtHardwareId.Text = hwInfo.ShortId;

                if (!hwInfo.IsValid)
                {
                    ShowStatus("⚠️ Donanım kimliği düşük güvenilirlikte.", isError: true);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Hardware ID yükleme hatası");
                txtHardwareId.Text = "Yüklenemedi";
                ShowStatus("Donanım kimliği alınamadı.", isError: true);
            }
        }

        // HATA DÜZELTME 1: Metot adı XAML ile eşitlendi (BtnCopyHardwareId_Click)
        private void BtnCopyHardwareId_Click(object sender, RoutedEventArgs e)
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

        // HATA DÜZELTME 2: Eksik olan İptal metodu eklendi
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TxtLicenseKey_TextChanged(object sender, TextChangedEventArgs e)
        {
            var key = txtLicenseKey.Text?.Trim() ?? "";

            if (key.Length > 0 && !key.Contains("-"))
            {
                key = FormatLicenseKey(key.Replace("-", ""));
                var caretPos = txtLicenseKey.CaretIndex;
                txtLicenseKey.Text = key;
                txtLicenseKey.CaretIndex = Math.Min(caretPos + 1, key.Length);
            }

            btnActivate.IsEnabled = LicenseKeyFormat.Validate(key) && !_isActivating;

            if (StatusBorder.Visibility == Visibility.Visible)
            {
                StatusBorder.Visibility = Visibility.Collapsed;
            }
        }

        private static string FormatLicenseKey(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            raw = raw.Replace("-", "").Replace(" ", "").ToUpperInvariant();
            var result = "";
            for (int i = 0; i < raw.Length && i < 25; i++)
            {
                if (i > 0 && i % 5 == 0) result += "-";
                result += raw[i];
            }
            return result;
        }

        private async void BtnActivate_Click(object sender, RoutedEventArgs e)
        {
            if (_isActivating) return;

            var licenseKey = txtLicenseKey.Text?.Trim() ?? "";

            if (string.IsNullOrEmpty(licenseKey))
            {
                ShowStatus("Lütfen lisans anahtarını girin.", isError: true);
                return;
            }

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
                    MessageBox.Show($"Aktivasyon başarılı!\n\nTür: {result.License.Type}", "Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    Log.Warning("Aktivasyon başarısız: {Message}", result.Message);
                    ShowStatus($"Aktivasyon başarısız: {result.Message}", isError: true);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Aktivasyon hatası");
                ShowStatus($"Hata: {ex.Message}", isError: true);
            }
            finally
            {
                _isActivating = false;
                btnActivate.IsEnabled = LicenseKeyFormat.Validate(txtLicenseKey.Text ?? "");
                progressBar.Visibility = Visibility.Collapsed;
            }
        }

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
            catch
            {
                MessageBox.Show("Tarayıcı açılamadı.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

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