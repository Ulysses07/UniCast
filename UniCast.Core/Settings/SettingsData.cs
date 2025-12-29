using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniCast.Core.Core;
using UniCast.Core.Models;

namespace UniCast.Core.Settings
{
    public sealed class SettingsData
    {
        // --- Cihaz Seçimleri ---
        public string? DefaultCamera { get; set; }
        public string? DefaultMicrophone { get; set; }
        public CaptureSource CaptureSource { get; set; } = CaptureSource.Camera;
        public string? SelectedVideoDevice { get; set; }
        public string? SelectedAudioDevice { get; set; }

        // --- Encoder & Kalite ---
        public string Encoder { get; set; } = "libx264";
        public int VideoKbps { get; set; } = 2500;
        public int AudioKbps { get; set; } = 128;

        // Ses Gecikmesi (Milisaniye) - Dudak senkronu için
        public int AudioDelayMs { get; set; } = 0;
        public List<OverlayItem> SceneItems { get; set; } = [];

        // --- Çözünürlük & FPS ---
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public int Fps { get; set; } = 30;

        /// <summary>
        /// Kamera döndürme açısı (derece).
        /// Kullanım: Kamerayı fiziksel olarak 90° döndürüp, yazılımda -90° döndürerek
        /// tam dikey (9:16) görüntü elde edilebilir.
        /// Değerler: 0 (döndürme yok), 90, 180, 270 (veya -90)
        /// </summary>
        public int CameraRotation { get; set; } = 0;


        // --- Yerel Kayıt ---
        public string? RecordFolder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "UniCast");
        public bool EnableLocalRecord { get; set; } = false;

        // --- Sosyal/Platform Alanları ---
        public string? YouTubeApiKey { get; set; }
        public string? YouTubeChannelId { get; set; }
        public string? YouTubeVideoId { get; set; }
        public string? TikTokRoomId { get; set; }

        // Instagram Okuyucu Hesap Ayarları
        public string? InstagramUserId { get; set; }
        public string? InstagramSessionId { get; set; }  // Şifre olarak kullanılıyor

        // Twitch Chat Ayarları
        public string? TwitchChannelName { get; set; }
        public string? TwitchOAuthToken { get; set; }
        public string? TwitchBotUsername { get; set; }

        public string? TikTokSessionCookie { get; set; }

        // ============================================
        // FACEBOOK OKUYUCU HESAP AYARLARI (YENİ)
        // ============================================
        // ⚠️ ÖNEMLİ: Ana hesabınızı DEĞİL, sadece chat okumak için
        // oluşturduğunuz AYRI BİR OKUYUCU HESAP bilgilerini girin!

        /// <summary>
        /// Facebook okuyucu hesap e-posta adresi veya telefon numarası.
        /// Ana hesabınızı DEĞİL, ayrı bir okuyucu hesap kullanın!
        /// </summary>
        public string? FacebookReaderEmail { get; set; }

        /// <summary>
        /// Facebook okuyucu hesap şifresi.
        /// DPAPI ile şifrelenerek saklanır.
        /// </summary>
        public string? FacebookReaderPassword { get; set; }

        /// <summary>
        /// Facebook canlı yayın URL'si.
        /// Örnek: https://www.facebook.com/sayfa/videos/123456789
        /// </summary>
        public string? FacebookLiveVideoUrl { get; set; }

        /// <summary>
        /// Facebook okuyucu hesap bağlantı durumu.
        /// true = giriş yapılmış, false = giriş yapılmamış
        /// </summary>
        public bool FacebookReaderConnected { get; set; } = false;

        // ============================================
        // FACEBOOK GRAPH API AYARLARI (YENİ - API Tabanlı)
        // ============================================
        // Resmi Graph API kullanarak yorum çekmek için.
        // Gereksinim: Facebook Sayfası (60+ gün, 100+ takipçi)

        /// <summary>
        /// Facebook Sayfa ID'si.
        /// /me/accounts endpoint'inden alınır.
        /// </summary>
        public string? FacebookPageId_Api { get; set; }

        /// <summary>
        /// Facebook Sayfa Adı.
        /// </summary>
        public string? FacebookPageName_Api { get; set; }

        /// <summary>
        /// Facebook Page Access Token.
        /// pages_read_engagement izni gerekli.
        /// </summary>
        public string? FacebookPageAccessToken { get; set; }

        /// <summary>
        /// Facebook User Access Token (Long-lived).
        /// Page token almak için kullanılır.
        /// </summary>
        public string? FacebookUserAccessToken { get; set; }

        /// <summary>
        /// Token son kullanma tarihi.
        /// </summary>
        public DateTime? FacebookTokenExpiry { get; set; }

        /// <summary>
        /// API tabanlı mı yoksa scraping tabanlı mı?
        /// true = Graph API, false = WebView2 Scraping
        /// </summary>
        public bool FacebookUseGraphApi { get; set; } = false;

        // --- Eski Facebook Ayarları (Geriye Dönük Uyumluluk) ---
        // NOT: Bu alanlar artık kullanılmıyor, ama mevcut ayarları bozmamak için tutuluyor
        [Obsolete("FacebookAccessToken artık kullanılmıyor. FacebookReaderEmail/Password kullanın.")]
        public string? FacebookAccessToken { get; set; }

        [Obsolete("FacebookPageId artık kullanılmıyor.")]
        public string? FacebookPageId { get; set; }

        [Obsolete("FacebookLiveVideoId artık kullanılmıyor. FacebookLiveVideoUrl kullanın.")]
        public string? FacebookLiveVideoId { get; set; }

        [Obsolete("FacebookCookies artık kullanılmıyor. Shared WebView2 profil kullanılıyor.")]
        public string? FacebookCookies { get; set; }

        [Obsolete("FacebookUserId artık kullanılmıyor.")]
        public string? FacebookUserId { get; set; }

        // --- Overlay Ayarları (Ayrı Pencere) ---
        public bool ShowOverlay { get; set; } = false;
        public int OverlayX { get; set; } = 24;
        public int OverlayY { get; set; } = 24;
        public double OverlayWidth { get; set; } = 300;
        public double OverlayHeight { get; set; } = 400;
        public double OverlayOpacity { get; set; } = 0.85;
        public int OverlayFontSize { get; set; } = 18;

        // --- Stream Chat Overlay Ayarları (Yayına Gömülü) ---
        /// <summary>
        /// Chat overlay'i yayına ekle (tüm platformlarda görünür)
        /// </summary>
        public bool StreamChatOverlayEnabled { get; set; } = false;

        /// <summary>
        /// Overlay pozisyonu: TopLeft, TopRight, BottomLeft, BottomRight, Center
        /// </summary>
        public string StreamChatOverlayPosition { get; set; } = "BottomLeft";

        /// <summary>
        /// Maksimum görünür mesaj sayısı (1-15)
        /// </summary>
        public int StreamChatOverlayMaxMessages { get; set; } = 8;

        /// <summary>
        /// Mesaj görünürlük süresi (saniye, 5-120)
        /// </summary>
        public int StreamChatOverlayMessageLifetime { get; set; } = 30;

        /// <summary>
        /// Font boyutu (12-48)
        /// </summary>
        public int StreamChatOverlayFontSize { get; set; } = 18;

        /// <summary>
        /// Şeffaflık (0.1-1.0)
        /// </summary>
        public double StreamChatOverlayOpacity { get; set; } = 0.9;

        /// <summary>
        /// Gölge efekti aktif mi
        /// </summary>
        public bool StreamChatOverlayShadow { get; set; } = true;

        // --- Profil Yönetimi ---
        public List<Profile> Profiles { get; set; } =
        [
            Profile.Default()
        ];

        public string? SelectedProfileName { get; set; } = Profile.Default().Name;

        public Profile GetSelectedProfile()
        {
            var p = Profiles.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(SelectedProfileName) &&
                string.Equals(x.Name, SelectedProfileName, StringComparison.OrdinalIgnoreCase));

            return p ?? Profile.Default();
        }

        public void Normalize()
        {
            // Overlay değerlerini mantıklı sınırlarda tut
            OverlayOpacity = Math.Clamp(OverlayOpacity, 0.0, 1.0);
            OverlayFontSize = Math.Clamp(OverlayFontSize, 8, 96);
            OverlayWidth = Math.Clamp(OverlayWidth, 200, 1000);
            OverlayHeight = Math.Clamp(OverlayHeight, 100, 1000);

            // Stream Chat Overlay ayarlarını normalize et
            StreamChatOverlayMaxMessages = Math.Clamp(StreamChatOverlayMaxMessages, 1, 15);
            StreamChatOverlayMessageLifetime = Math.Clamp(StreamChatOverlayMessageLifetime, 5, 120);
            StreamChatOverlayFontSize = Math.Clamp(StreamChatOverlayFontSize, 12, 48);
            StreamChatOverlayOpacity = Math.Clamp(StreamChatOverlayOpacity, 0.1, 1.0);

            // Geçerli pozisyon kontrolü
            var validPositions = new[] { "TopLeft", "TopRight", "BottomLeft", "BottomRight", "Center" };
            if (!validPositions.Contains(StreamChatOverlayPosition))
            {
                StreamChatOverlayPosition = "BottomLeft";
            }

            SceneItems ??= new List<OverlayItem>();
            if (OverlayX < 0) OverlayX = 0;
            if (OverlayY < 0) OverlayY = 0;
            if (AudioDelayMs < 0) AudioDelayMs = 0;

            // Camera rotation normalization (0, 90, 180, 270)
            CameraRotation = CameraRotation switch
            {
                90 or -270 => 90,
                180 or -180 => 180,
                270 or -90 => 270,
                _ => 0
            };

            if (!SceneItems.Any(x => x.Type == OverlayType.Chat))
            {
                SceneItems.Add(new OverlayItem
                {
                    Type = OverlayType.Chat,
                    X = 24,
                    Y = 24,
                    Width = 300,
                    Height = 400,
                    IsVisible = true
                });
            }
        }

        /// <summary>
        /// Facebook okuyucu hesap bilgilerinin girilip girilmediğini kontrol eder.
        /// </summary>
        public bool HasFacebookReaderCredentials()
        {
            return !string.IsNullOrWhiteSpace(FacebookReaderEmail) &&
                   !string.IsNullOrWhiteSpace(FacebookReaderPassword);
        }

        /// <summary>
        /// Facebook okuyucu hesap bilgilerini temizler (çıkış yap).
        /// </summary>
        public void ClearFacebookReaderCredentials()
        {
            FacebookReaderEmail = null;
            FacebookReaderPassword = null;
            FacebookReaderConnected = false;
        }

        /// <summary>
        /// Facebook Graph API bilgilerinin girilip girilmediğini kontrol eder.
        /// </summary>
        public bool HasFacebookApiCredentials()
        {
            return !string.IsNullOrWhiteSpace(FacebookPageId_Api) &&
                   !string.IsNullOrWhiteSpace(FacebookPageAccessToken);
        }

        /// <summary>
        /// Facebook API token'ın geçerli olup olmadığını kontrol eder.
        /// </summary>
        public bool IsFacebookApiTokenValid()
        {
            if (!HasFacebookApiCredentials())
                return false;

            // Expiry yoksa veya gelecekteyse geçerli kabul et
            if (!FacebookTokenExpiry.HasValue)
                return true;

            return FacebookTokenExpiry.Value > DateTime.UtcNow;
        }

        /// <summary>
        /// Facebook Graph API bilgilerini temizler.
        /// </summary>
        public void ClearFacebookApiCredentials()
        {
            FacebookPageId_Api = null;
            FacebookPageName_Api = null;
            FacebookPageAccessToken = null;
            FacebookUserAccessToken = null;
            FacebookTokenExpiry = null;
            FacebookUseGraphApi = false;
        }
    }
}