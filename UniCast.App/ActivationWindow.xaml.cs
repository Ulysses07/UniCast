using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Serilog;
using UniCast.Licensing;
using UniCast.Licensing.Crypto;
using UniCast.Licensing.Hardware;
using UniCast.Licensing.Models;
using ColorConverter = System.Windows.Media.ColorConverter;
using MessageBox = System.Windows.MessageBox;

namespace UniCast.App.Views
{
    /// <summary>
    /// Lisans aktivasyon penceresi.
    /// </summary>
    public partial class ActivationWindow : Window
    {
        private bool _isActivating;

        public ActivationWindow()
        {
            InitializeComponent();
            LoadHardwareId();
        }

        private void LoadHardwareId()
        {
            try
            {
                var shortId = HardwareFingerprint.GenerateShort();
                HardwareIdText.Text = FormatHardwareId(shortId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ActivationWindow] Hardware ID yüklenemedi");
                HardwareIdText.Text = "Yüklenemedi";
            }
        }

        private static string FormatHardwareId(string id)
        {
            // Her 4 karakterde bir tire ekle
            if (string.IsNullOrEmpty(id))
                return "";

            var formatted = "";
            for (int i = 0; i < id.Length; i++)
            {
                if (i > 0 && i % 4 == 0)
                    formatted += "-";
                formatted += id[i];
            }

            return formatted;
        }

        private void LicenseKeyTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var text = LicenseKeyTextBox.Text;

            // Otomatik tire ekleme
            if (text.Length > 0 && !text.Contains('-'))
            {
                var clean = text.Replace("-", "").ToUpperInvariant();

                if (clean.Length > 5)
                {
                    var formatted = "";
                    for (int i = 0; i < clean.Length && i < 25; i++)
                    {
                        if (i > 0 && i % 5 == 0)
                            formatted += "-";
                        formatted += clean[i];
                    }

                    LicenseKeyTextBox.Text = formatted;
                    LicenseKeyTextBox.CaretIndex = formatted.Length;
                }
            }

            // Aktivasyon butonu kontrolü
            var isValidFormat = LicenseKeyFormat.Validate(LicenseKeyTextBox.Text);
            ActivateButton.IsEnabled = isValidFormat && !_isActivating;

            // Geçersiz format uyarısı
            if (LicenseKeyTextBox.Text.Length >= 29 && !isValidFormat)
            {
                ShowStatus("⚠️", "Geçersiz lisans anahtarı formatı", "#FFA500");
            }
            else
            {
                HideStatus();
            }
        }

        private async void ActivateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isActivating)
                return;

            var licenseKey = LicenseKeyTextBox.Text.Trim();

            if (!LicenseKeyFormat.Validate(licenseKey))
            {
                ShowStatus("❌", "Geçersiz lisans anahtarı formatı", "#FF4444");
                return;
            }

            await ActivateLicenseAsync(licenseKey);
        }

        private async Task ActivateLicenseAsync(string licenseKey)
        {
            _isActivating = true;
            ActivateButton.IsEnabled = false;
            LicenseKeyTextBox.IsEnabled = false;
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingText.Text = "Lisans doğrulanıyor...";

            try
            {
                Log.Information("[ActivationWindow] Aktivasyon başlatılıyor: {Key}",
                    LicenseKeyFormat.Mask(licenseKey));

                var result = await LicenseManager.Instance.ActivateAsync(licenseKey);

                if (result.IsValid)
                {
                    Log.Information("[ActivationWindow] Aktivasyon başarılı: {Type}",
                        result.License?.Type);

                    LoadingOverlay.Visibility = Visibility.Collapsed;

                    MessageBox.Show(
                        $"Lisans başarıyla aktifleştirildi!\n\n" +
                        $"Tür: {GetLicenseTypeName(result.License?.Type ?? LicenseType.Trial)}\n" +
                        $"Süre: {result.License?.DaysRemaining} gün kaldı\n" +
                        $"Sahip: {result.License?.LicenseeName}",
                        "Aktivasyon Başarılı",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    DialogResult = true;
                    Close();
                }
                else
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;

                    var errorMessage = result.Status switch
                    {
                        LicenseStatus.InvalidSignature => "Geçersiz lisans anahtarı. Lütfen kontrol edin.",
                        LicenseStatus.Expired => "Bu lisansın süresi dolmuş.",
                        LicenseStatus.Revoked => "Bu lisans iptal edilmiş.",
                        LicenseStatus.MachineLimitExceeded => "Maksimum makine sayısına ulaşıldı.",
                        LicenseStatus.ServerUnreachable => "Lisans sunucusuna bağlanılamadı. İnternet bağlantınızı kontrol edin.",
                        _ => result.Message ?? "Bilinmeyen hata"
                    };

                    ShowStatus("❌", errorMessage, "#FF4444");
                    Log.Warning("[ActivationWindow] Aktivasyon başarısız: {Status} - {Message}",
                        result.Status, result.Message);
                }
            }
            catch (Exception ex)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                ShowStatus("❌", $"Hata: {ex.Message}", "#FF4444");
                Log.Error(ex, "[ActivationWindow] Aktivasyon hatası");
            }
            finally
            {
                _isActivating = false;
                ActivateButton.IsEnabled = LicenseKeyFormat.Validate(LicenseKeyTextBox.Text);
                LicenseKeyTextBox.IsEnabled = true;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowStatus(string icon, string message, string color)
        {
            StatusIcon.Text = icon;
            StatusText.Text = message;
            StatusBorder.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(color + "33")); // %20 opaklık
            StatusBorder.Visibility = Visibility.Visible;
        }

        private void HideStatus()
        {
            StatusBorder.Visibility = Visibility.Collapsed;
        }

        private static string GetLicenseTypeName(LicenseType type)
        {
            return type switch
            {
                LicenseType.Trial => "Deneme",
                LicenseType.Personal => "Kişisel",
                LicenseType.Professional => "Profesyonel",
                LicenseType.Business => "İşletme",
                LicenseType.Enterprise => "Kurumsal",
                LicenseType.MonthlySubscription => "Aylık Abonelik",
                LicenseType.YearlySubscription => "Yıllık Abonelik",
                LicenseType.Lifetime => "Ömür Boyu",
                LicenseType.Educational => "Eğitim",
                LicenseType.NFR => "NFR",
                _ => type.ToString()
            };
        }
    }
}