using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniCast.Core.Models;

namespace UniCast.Core.Settings
{
    /// <summary>
    /// Uygulamanın genel ayarları: cihaz seçimleri, encode, sosyal hedef bilgileri vb.
    /// ViewModel’lerin beklediği tüm alanlar eklendi.
    /// </summary>
    public sealed class SettingsData
    {
        // --- Cihaz seçimleri ---
        public string? DefaultCamera { get; set; }          // dshow: video="..."
        public string? DefaultMicrophone { get; set; }      // dshow: audio="..."
        public CaptureSource CaptureSource { get; set; } = CaptureSource.Camera;
        public string? SelectedVideoDevice { get; set; }  // örn: video="USB2.0 Camera"
        public string? SelectedAudioDevice { get; set; }  // örn: audio="Mikrofon (USB Audio Device)"

        // --- Encoder & kalite ---
        /// <summary>libx264 | h264_nvenc | hevc_nvenc | libx265 vb.</summary>
        public string Encoder { get; set; } = "libx264";
        public int VideoKbps { get; set; } = 2500;
        public int AudioKbps { get; set; } = 128;

        // --- Çözünürlük & fps ---
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public int Fps { get; set; } = 30;

        // --- Yerel kayıt ---
        public string? RecordFolder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "UniCast");
        public bool EnableLocalRecord { get; set; } = false;

        // --- Sosyal/Platform alanları (ViewModel’ler istiyor) ---
        public string? YouTubeApiKey { get; set; }
        public string? YouTubeChannelId { get; set; }
        public string? TikTokRoomId { get; set; }
        public string? InstagramUserId { get; set; }
        public string? InstagramSessionId { get; set; }
        public string? FacebookAccessToken { get; set; }
        public string? FacebookPageId { get; set; }
        public string? FacebookLiveVideoId { get; set; }
        public bool ShowOverlay { get; set; } = false;
        public int OverlayX { get; set; } = 24;     // piksel, sol üst x
        public int OverlayY { get; set; } = 24;     // piksel, sol üst y
        public double OverlayOpacity { get; set; } = 0.85;  // 0.0 - 1.0
        public int OverlayFontSize { get; set; } = 18;      // px / pt
        public string? TikTokSessionCookie { get; set; }

        // --- Profil yönetimi ---
        public List<Profile> Profiles { get; set; } = new()
        {
            Profile.Default()
        };

        /// <summary>Seçili profilin adı (UI bu string’i saklıyor)</summary>
        public string? SelectedProfileName { get; set; } = Profile.Default().Name;

        /// <summary>Seçili profili getir; yoksa varsayılanı döner.</summary>
        public Profile GetSelectedProfile()
        {
            var p = Profiles.FirstOrDefault((Func<Profile, bool>)(x =>
                !string.IsNullOrWhiteSpace(SelectedProfileName) &&
                string.Equals((string)x.Name, SelectedProfileName, StringComparison.OrdinalIgnoreCase)));

            return p ?? Profile.Default();
        }
        public void Normalize()
        {
            // .NET 8'de Math.Clamp hazır.
            OverlayOpacity = Math.Clamp(OverlayOpacity, 0.0, 1.0);
            OverlayFontSize = Math.Clamp(OverlayFontSize, 8, 96);
            if (OverlayX < 0) OverlayX = 0;
            if (OverlayY < 0) OverlayY = 0;
        }
    }
}
