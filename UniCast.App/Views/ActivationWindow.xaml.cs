using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using UniCast.App.Infrastructure;
using UniCast.Licensing;
using UniCast.Licensing.Hardware;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;


namespace UniCast.App.Views
{
    /// <summary>
    /// Lisans aktivasyon penceresi
    /// </summary>
    public partial class ActivationWindow : Window
    {
        private readonly LicenseManager _licenseManager;

        public bool IsActivated { get; private set; }

        public ActivationWindow()
        {
            InitializeComponent();
            _licenseManager = LicenseManager.Instance;
            LoadHardwareId();
        }

        private void LoadHardwareId()
        {
            try
            {
                var hardwareId = HardwareFingerprint.Generate();
                txtHardwareId.Text = hardwareId.Length > 32
                    ? hardwareId.Substring(0, 32) + "..."
                    : hardwareId;
                txtHardwareId.Tag = hardwareId; // Tam ID'yi sakla
            }
            catch (Exception ex)
            {
                txtHardwareId.Text = "Donanım kimliği alınamadı";
                ShowStatus($"Hata: {ex.Message}", false);
            }
        }

        private void CopyHardwareId_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var fullId = txtHardwareId.Tag?.ToString() ?? txtHardwareId.Text;
                Clipboard.SetText(fullId);
                ShowStatus("Donanım kimliği panoya kopyalandı!", true);
            }
            catch
            {
                ShowStatus("Kopyalama başarısız oldu.", false);
            }
        }

        private void TxtLicenseKey_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Auto-format: Add dashes
            var text = txtLicenseKey.Text.Replace("-", "").ToUpper();
            if (text.Length > 0)
            {
                var formatted = "";
                for (int i = 0; i < text.Length && i < 25; i++)
                {
                    if (i > 0 && i % 5 == 0)
                        formatted += "-";
                    formatted += text[i];
                }

                if (formatted != txtLicenseKey.Text)
                {
                    txtLicenseKey.Text = formatted;
                    txtLicenseKey.CaretIndex = formatted.Length;
                }
            }

            // Enable button if key looks valid
            btnActivate.IsEnabled = txtLicenseKey.Text.Replace("-", "").Length == 25;
        }

        // DÜZELTME v20: AsyncVoidHandler ile güvenli async event handler
        private void Activate_Click(object sender, RoutedEventArgs e)
        {
            AsyncVoidHandler.Handle(
                async () => await ActivateAsync(),
                showErrorDialog: true);
        }

        private async Task ActivateAsync()
        {
            var licenseKey = txtLicenseKey.Text.Trim();

            if (string.IsNullOrWhiteSpace(licenseKey))
            {
                ShowStatus("Lütfen lisans anahtarınızı girin.", false);
                return;
            }

            btnActivate.IsEnabled = false;
            progressBar.Visibility = Visibility.Visible;
            progressBar.IsIndeterminate = true;

            try
            {
                ShowStatus("Lisans doğrulanıyor...", true);

                var result = await Task.Run(() => _licenseManager.ActivateLicense(licenseKey));

                if (result.Success)
                {
                    IsActivated = true;
                    ShowStatus("✓ Lisans başarıyla aktive edildi!", true);

                    await Task.Delay(1500);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    ShowStatus($"✗ {result.ErrorMessage}", false);
                }
            }
            finally
            {
                btnActivate.IsEnabled = true;
                progressBar.Visibility = Visibility.Collapsed;
                progressBar.IsIndeterminate = false;
            }
        }

        private void ShowStatus(string message, bool isSuccess)
        {
            txtStatus.Text = message;
            txtStatus.Foreground = new SolidColorBrush(isSuccess ? Colors.LightGreen : Colors.Salmon);

            StatusBorder.Background = new SolidColorBrush(
                isSuccess ? Color.FromArgb(40, 0, 255, 0) : Color.FromArgb(40, 255, 0, 0));
            StatusBorder.BorderBrush = new SolidColorBrush(
                isSuccess ? Colors.DarkGreen : Colors.DarkRed);
            StatusBorder.BorderThickness = new Thickness(1);
            StatusBorder.Visibility = Visibility.Visible;
        }

        private void BuyLicense_Click(object sender, RoutedEventArgs e)
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
                // DÜZELTME v26: Boş catch'e loglama eklendi
                System.Diagnostics.Debug.WriteLine($"[ActivationWindow.BuyNow_Click] URL açma hatası: {ex.Message}");
            }
        }

        private void TrialMode_Click(object sender, RoutedEventArgs e)
        {
            IsActivated = false;
            DialogResult = true;
            Close();
        }
    }
}