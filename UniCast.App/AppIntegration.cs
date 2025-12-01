using System.Windows;
using UniCast.Licensing;
using UniCast.Licensing.Models;
using MessageBox = System.Windows.MessageBox;

namespace UniCast.App
{
    /// <summary>
    /// Lisans kontrolü için yardımcı sınıf.
    /// Tüm özellikler herkese açık - sadece geçerli lisans gerekli.
    /// </summary>
    public static class LicenseGate
    {
        /// <summary>
        /// Lisansın geçerli olup olmadığını kontrol eder.
        /// </summary>
        public static bool IsValid()
        {
            return LicenseManager.Instance.IsLicenseValid();
        }

        /// <summary>
        /// Lisans geçerli değilse mesaj gösterir ve false döner.
        /// Kullanım: if (!LicenseGate.RequireValidLicense()) return;
        /// </summary>
        public static bool RequireValidLicense(string actionName = "Bu işlem")
        {
            if (IsValid())
                return true;

            var info = LicenseManager.Instance.GetLicenseInfo();

            string message = info.Status == LicenseStatus.Expired
                ? "Deneme süreniz doldu.\n\nDevam etmek için lisans satın alın."
                : $"{actionName} için geçerli bir lisans gerekli.\n\nLisans satın alın veya deneme sürümünü başlatın.";

            MessageBox.Show(
                message,
                "Lisans Gerekli",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return false;
        }

        /// <summary>
        /// Destek süresinin aktif olup olmadığını kontrol eder.
        /// </summary>
        public static bool IsSupportActive()
        {
            return LicenseManager.Instance.IsSupportActive();
        }
    }

    /// <summary>
    /// Watermark kontrolü için yardımcı sınıf.
    /// Sadece Trial'da watermark gösterilir.
    /// </summary>
    public static class WatermarkHelper
    {
        /// <summary>
        /// Watermark gösterilmeli mi?
        /// Sadece Trial lisansta watermark var.
        /// </summary>
        public static bool ShouldShowWatermark()
        {
            var info = LicenseManager.Instance.GetLicenseInfo();
            return info.Type == LicenseType.Trial;
        }

        /// <summary>
        /// Watermark metni.
        /// </summary>
        public static string GetWatermarkText()
        {
            var info = LicenseManager.Instance.GetLicenseInfo();

            if (info.Type == LicenseType.Trial)
                return $"UniCast Trial - {info.DaysRemaining} gün kaldı";

            return ""; // Lifetime'da watermark yok
        }

        /// <summary>
        /// Watermark opacity değeri (0.0 - 1.0).
        /// </summary>
        public static double GetWatermarkOpacity()
        {
            return 0.7; // Trial'da görünür
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
                LicenseType.Trial => "Deneme Sürümü (14 gün)",
                LicenseType.Lifetime => "Ömür Boyu Lisans",
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
                LicenseStatus.SupportExpired => "Destek Süresi Dolmuş",
                LicenseStatus.NotFound => "Lisans Yok",
                LicenseStatus.HardwareMismatch => "Donanım Uyumsuz",
                LicenseStatus.InvalidSignature => "Geçersiz İmza",
                LicenseStatus.Revoked => "İptal Edilmiş",
                LicenseStatus.Tampered => "Güvenlik İhlali",
                _ => "Bilinmeyen"
            };
        }

        /// <summary>
        /// Destek durumu metnini döner.
        /// </summary>
        public static string GetSupportStatusText()
        {
            var info = LicenseManager.Instance.GetLicenseInfo();

            if (info.Type == LicenseType.Trial)
                return "Deneme sürümü";

            if (info.IsSupportActive)
                return $"Aktif ({info.SupportDaysRemaining} gün kaldı)";

            return "Süresi dolmuş - Yenileyin";
        }
    }
}