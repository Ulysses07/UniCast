using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UniCast.Licensing;
using UniCast.Licensing.Hardware; // HardwareFingerprint için gerekli
using Serilog;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;

namespace UniCast.App
{
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
                // HardwareFingerprint sınıfı UniCast.Licensing projesinden geliyor
                var hwInfo = HardwareFingerprint.Validate();
                txtHardwareId.Text = hwInfo.ShortId;

                if (!hwInfo.IsValid)
                {
                    ShowStatus("⚠️ Donanım kimliği güvenilirliği düşük.", isError: true);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Hardware ID yüklenemedi");
                txtHardwareId.Text = "HATA";
            }
        }

        // HATA DÜZELTME 1: XAML'daki 'BtnCopyHardwareId_Click' ismine uygun metot
        private void BtnCopyHardwareId_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(txtHardwareId.Text))
                {
                    Clipboard.SetText(txtHardwareId.Text);
                    ShowStatus("✓ Kopyalandı!", isError: false);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Kopyalama hatası");
            }
        }

        // HATA DÜZELTME 2: XAML'daki 'BtnCancel_Click' ismine uygun metot
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private async void BtnActivate_Click(object sender, RoutedEventArgs e)
        {
            if (_isActivating) return;

            var key = txtLicenseKey.Text?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                ShowStatus("Lütfen anahtar girin.", isError: true);
                return;
            }

            _isActivating = true;
            btnActivate.IsEnabled = false;
            progressBar.Visibility = Visibility.Visible;
            ShowStatus("Aktivasyon yapılıyor...", isError: false);

            try
            {
                // LicenseManager üzerinden aktivasyon
                var result = await LicenseManager.Instance.ActivateAsync(key);

                if (result.IsValid)
                {
                    MessageBox.Show("Lisans başarıyla aktif edildi!", "Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    ShowStatus($"Hata: {result.Message}", isError: true);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Aktivasyon hatası");
                ShowStatus("Sunucu bağlantı hatası.", isError: true);
            }
            finally
            {
                _isActivating = false;
                btnActivate.IsEnabled = true;
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
            catch { }
        }

        private void TxtLicenseKey_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Basit formatlama (Tire ekleme vb.)
            // İstersen burayı detaylandırabilirsin
            if (StatusBorder.Visibility == Visibility.Visible)
                StatusBorder.Visibility = Visibility.Collapsed;
        }

        private void ShowStatus(string msg, bool isError)
        {
            txtStatus.Text = msg;
            StatusBorder.Background = isError
                ? new SolidColorBrush(Color.FromRgb(0x5C, 0x1A, 0x1A)) // Kırmızımsı
                : new SolidColorBrush(Color.FromRgb(0x0F, 0x34, 0x60)); // Mavimsi
            StatusBorder.Visibility = Visibility.Visible;
        }
    }
}