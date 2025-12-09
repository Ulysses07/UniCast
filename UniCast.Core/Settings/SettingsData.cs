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

        // --- Overlay Ayarları ---
        public bool ShowOverlay { get; set; } = false;
        public int OverlayX { get; set; } = 24;
        public int OverlayY { get; set; } = 24;
        public double OverlayWidth { get; set; } = 300;
        public double OverlayHeight { get; set; } = 400;
        public double OverlayOpacity { get; set; } = 0.85;
        public int OverlayFontSize { get; set; } = 18;

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
            // Değerleri mantıklı sınırlarda tut
            OverlayOpacity = Math.Clamp(OverlayOpacity, 0.0, 1.0);
            OverlayFontSize = Math.Clamp(OverlayFontSize, 8, 96);
            OverlayWidth = Math.Clamp(OverlayWidth, 200, 1000);
            OverlayHeight = Math.Clamp(OverlayHeight, 100, 1000);

            SceneItems ??= new List<OverlayItem>();
            if (OverlayX < 0) OverlayX = 0;
            if (OverlayY < 0) OverlayY = 0;
            if (AudioDelayMs < 0) AudioDelayMs = 0;
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
    }
}