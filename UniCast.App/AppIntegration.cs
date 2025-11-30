using System.Windows;
using UniCast.Licensing;
using UniCast.Licensing.Models;
using MessageBox = System.Windows.MessageBox;

namespace UniCast.App
{
    /// <summary>
    /// Özellik bazlı erişim kontrolü.
    /// UI tarafında özellik kısıtlaması için kullanılır.
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
        /// Özellik aktif değilse mesaj gösterir ve false döner.
        /// Kullanım: if (!FeatureGate.RequireFeature(...)) return;
        /// </summary>
        public static bool RequireFeature(LicenseFeatures feature, string featureName)
        {
            if (IsEnabled(feature))
                return true;

            var info = LicenseManager.Instance.GetLicenseInfo();

            string message = info.Type == LicenseType.Trial
                ? $"'{featureName}' özelliği deneme sürümünde kullanılamaz.\n\n" +
                  "Tüm özelliklere erişmek için lisans satın alın."
                : $"'{featureName}' özelliği mevcut lisansınızda bulunmuyor.\n\n" +
                  "Bu özelliği kullanmak için lisansınızı yükseltin.";

            MessageBox.Show(
                message,
                "Özellik Kısıtlı",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return false;
        }

        /// <summary>
        /// Özellik aktif değilse sessizce false döner (mesaj göstermez).
        /// </summary>
        public static bool CheckFeatureSilent(LicenseFeatures feature)
        {
            return IsEnabled(feature);
        }
    }

    /// <summary>
    /// Watermark kontrolü için yardımcı sınıf.
    /// Trial ve bazı lisanslarda watermark gösterilir.
    /// </summary>
    public static class WatermarkHelper
    {
        /// <summary>
        /// Watermark gösterilmeli mi?
        /// </summary>
        public static bool ShouldShowWatermark()
        {
            return !LicenseManager.Instance.HasFeature(LicenseFeatures.NoWatermark);
        }

        /// <summary>
        /// Watermark metni.
        /// </summary>
        public static string GetWatermarkText()
        {
            var info = LicenseManager.Instance.GetLicenseInfo();

            if (info.Type == LicenseType.Trial)
                return $"UniCast Trial - {info.DaysRemaining} gün kaldı";

            return "UniCast";
        }

        /// <summary>
        /// Watermark opacity değeri (0.0 - 1.0).
        /// </summary>
        public static double GetWatermarkOpacity()
        {
            var info = LicenseManager.Instance.GetLicenseInfo();
            return info.Type == LicenseType.Trial ? 0.7 : 0.3;
        }
    }

    /// <summary>
    /// Lisans bilgisi gösterimi için yardımcı.
    /// </summary>
    public static class LicenseDisplayHelper
    {
        /// <summary>
        /// Lisans türünü Türkçe string olarak döner.
        /// </summary>
        public static string GetLicenseTypeName(LicenseType type)
        {
            return type switch
            {
                LicenseType.Trial => "Deneme Sürümü",
                LicenseType.Personal => "Kişisel Lisans",
                LicenseType.Professional => "Profesyonel Lisans",
                LicenseType.Enterprise => "Kurumsal Lisans",
                _ => "Bilinmeyen"
            };
        }

        /// <summary>
        /// Lisans durumunu Türkçe string olarak döner.
        /// </summary>
        public static string GetStatusText(LicenseStatus status)
        {
            return status switch
            {
                LicenseStatus.Valid => "Geçerli",
                LicenseStatus.Expired => "Süresi Dolmuş",
                LicenseStatus.GracePeriod => "Çevrimdışı Mod",
                LicenseStatus.NotFound => "Lisans Yok",
                LicenseStatus.HardwareMismatch => "Donanım Uyumsuz",
                LicenseStatus.InvalidSignature => "Geçersiz İmza",
                LicenseStatus.Revoked => "İptal Edilmiş",
                LicenseStatus.Tampered => "Güvenlik İhlali",
                _ => "Bilinmeyen"
            };
        }
    }
}