using System;
using System.Threading.Tasks;
using System.Windows;
using UniCast.Licensing;
using UniCast.Licensing.Models;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace UniCast.App
{
    /// <summary>
    /// Lisans sisteminin WPF uygulamasına entegrasyonu.
    /// </summary>
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Lisans sistemini başlat
            var licenseResult = await InitializeLicenseAsync();

            if (!licenseResult.IsValid)
            {
                await HandleLicenseFailureAsync(licenseResult);
                return;
            }

            // Lisans olaylarını dinle
            LicenseManager.Instance.StatusChanged += OnLicenseStatusChanged;

            // Ana pencereyi aç
            var mainWindow = new MainWindow();
            mainWindow.Show();
        }

        private async Task<LicenseValidationResult> InitializeLicenseAsync()
        {
            try
            {
                // Splash screen gösterilebilir
                // new SplashWindow().Show();

                var result = await LicenseManager.Instance.InitializeAsync();

                // Lisans bulunamadıysa trial başlat veya aktivasyon iste
                if (result.Status == LicenseStatus.NotFound)
                {
                    var choice = MessageBox.Show(
                        "Lisans bulunamadı. Deneme sürümünü başlatmak ister misiniz?",
                        "UniCast - Lisans",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (choice == MessageBoxResult.Yes)
                    {
                        result = LicenseManager.Instance.StartTrial();
                    }
                    else
                    {
                        // Aktivasyon penceresini göster
                        var activationWindow = new ActivationWindow();
                        if (activationWindow.ShowDialog() == true)
                        {
                            result = await LicenseManager.Instance.ValidateAsync();
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                return LicenseValidationResult.Failure(
                    LicenseStatus.Tampered,
                    $"Lisans başlatma hatası: {ex.Message}");
            }
        }

        private async Task HandleLicenseFailureAsync(LicenseValidationResult result)
        {
            string message = result.Status switch
            {
                LicenseStatus.Expired => "Lisans süreniz dolmuş. Yenilemek için satın alma sayfasını ziyaret edin.",
                LicenseStatus.HardwareMismatch => "Bu lisans farklı bir bilgisayarda kullanılıyor. Devre dışı bırakın veya yeni lisans satın alın.",
                LicenseStatus.InvalidSignature => "Lisans doğrulanamadı. Lütfen destek ile iletişime geçin.",
                LicenseStatus.Revoked => "Bu lisans iptal edilmiş.",
                LicenseStatus.Tampered => "Güvenlik ihlali tespit edildi. Uygulama kapatılıyor.",
                LicenseStatus.MachineLimitExceeded => "Maksimum makine sayısına ulaşıldı.",
                _ => result.Message
            };

            MessageBox.Show(
                message,
                "UniCast - Lisans Hatası",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            // Ciddi hatalarda uygulamayı kapat
            if (result.Status == LicenseStatus.Tampered)
            {
                Shutdown(1);
                return;
            }

            // Aktivasyon penceresini göster
            var activationWindow = new ActivationWindow();
            if (activationWindow.ShowDialog() != true)
            {
                Shutdown(0);
            }
        }

        private void OnLicenseStatusChanged(object? sender, LicenseStatusChangedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                switch (e.NewStatus)
                {
                    case LicenseStatus.Expired:
                        MessageBox.Show("Lisans süreniz doldu!", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                        break;

                    case LicenseStatus.GracePeriod:
                        // Grace period bildirimi
                        break;

                    case LicenseStatus.Tampered:
                        MessageBox.Show("Güvenlik ihlali tespit edildi!", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                        Shutdown(1);
                        break;
                }
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            LicenseManager.Instance.Dispose();
            base.OnExit(e);
        }
    }

    /// <summary>
    /// Özellik bazlı erişim kontrolü örneği.
    /// </summary>
    public static class FeatureGate
    {
        /// <summary>
        /// Özelliğin kullanılabilir olup olmadığını kontrol eder.
        /// </summary>
        public static bool IsEnabled(LicenseFeatures feature)
        {
            return LicenseManager.Instance.HasFeature(feature);
        }

        /// <summary>
        /// Özellik aktif değilse mesaj gösterir.
        /// </summary>
        public static bool RequireFeature(LicenseFeatures feature, string featureName)
        {
            if (IsEnabled(feature))
                return true;

            var info = LicenseManager.Instance.GetLicenseInfo();

            string message = info.Type == LicenseType.Trial
                ? $"{featureName} özelliği deneme sürümünde kullanılamaz. Yükseltmek için satın alın."
                : $"{featureName} özelliği mevcut lisansınızda bulunmuyor. Yükseltme için iletişime geçin.";

            MessageBox.Show(message, "Özellik Kısıtlı", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
    }

    /// <summary>
    /// Kullanım örneği - StreamController içinde.
    /// </summary>
    public partial class StreamController
    {
        public void StartMultiStream()
        {
            // Çoklu yayın özelliği kontrolü
            if (!FeatureGate.RequireFeature(LicenseFeatures.MultiStream, "Çoklu Platform Yayını"))
                return;

            // Özellik aktif, devam et
            DoStartMultiStream();
        }

        public void EnableCustomOverlay()
        {
            if (!FeatureGate.RequireFeature(LicenseFeatures.CustomOverlay, "Özel Overlay"))
                return;

            DoEnableCustomOverlay();
        }

        private void DoStartMultiStream() { /* ... */ }
        private void DoEnableCustomOverlay() { /* ... */ }
    }

    /// <summary>
    /// Watermark kontrolü örneği.
    /// </summary>
    public static class WatermarkHelper
    {
        public static bool ShouldShowWatermark()
        {
            var info = LicenseManager.Instance.GetLicenseInfo();
            return !LicenseManager.Instance.HasFeature(LicenseFeatures.NoWatermark);
        }

        public static string GetWatermarkText()
        {
            var info = LicenseManager.Instance.GetLicenseInfo();

            if (info.Type == LicenseType.Trial)
                return $"UniCast Trial - {info.DaysRemaining} gün kaldı";

            return "UniCast";
        }
    }
}

/* ============================================================
 * ActivationWindow.xaml.cs - Aktivasyon penceresi
 * ============================================================ */

namespace UniCast.App
{
    using System.Windows;
    using System.Windows.Controls;
    using UniCast.Licensing;
    using UniCast.Licensing.Crypto;
    using UniCast.Licensing.Hardware;

    public partial class ActivationWindow : Window
    {
        public ActivationWindow()
        {
            InitializeComponent();
            LoadHardwareId();
        }

        private void LoadHardwareId()
        {
            var hwInfo = HardwareFingerprint.Validate();
            // txtHardwareId.Text = hwInfo.ShortId;
        }

        private async void BtnActivate_Click(object sender, RoutedEventArgs e)
        {
            // var licenseKey = txtLicenseKey.Text.Trim();
            var licenseKey = ""; // UI'dan al

            if (string.IsNullOrEmpty(licenseKey))
            {
                MessageBox.Show("Lütfen lisans anahtarını girin.");
                return;
            }

            // Format kontrolü
            if (!LicenseKeyFormat.Validate(licenseKey))
            {
                MessageBox.Show("Geçersiz lisans anahtarı formatı.");
                return;
            }

            // btnActivate.IsEnabled = false;
            // progressBar.Visibility = Visibility.Visible;

            try
            {
                var result = await LicenseManager.Instance.ActivateAsync(licenseKey);

                if (result.IsValid)
                {
                    MessageBox.Show(
                        $"Aktivasyon başarılı!\n\n" +
                        $"Tür: {result.License?.Type}\n" +
                        $"Kullanıcı: {result.License?.LicenseeName}\n" +
                        $"Bitiş: {result.License?.ExpiresAtUtc:dd.MM.yyyy}",
                        "Başarılı",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show(
                        $"Aktivasyon başarısız:\n{result.Message}",
                        "Hata",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            finally
            {
                // btnActivate.IsEnabled = true;
                // progressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnBuyLicense_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://unicast.app/buy",
                UseShellExecute = true
            });
        }
    }
}