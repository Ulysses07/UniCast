using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniCast.Core.Core;
using UniCast.Core.Models;

namespace UniCast.Core.Settings
{
    /// <summary>
    /// Uygulamanın genel ayarları: cihaz seçimleri, encode, sosyal hedef bilgileri vb.
    /// </summary>
    public sealed class SettingsData
    {
        // --- Cihaz seçimleri ---
        public string? DefaultCamera { get; set; }
        public string? DefaultMicrophone { get; set; }
        public CaptureSource CaptureSource { get; set; } = CaptureSource.Camera;
        public string? SelectedVideoDevice { get; set; }
        public string? SelectedAudioDevice { get; set; }

        // --- Encoder & kalite ---
        public string Encoder { get; set; } = "libx264";
        public int VideoKbps { get; set; } = 2500;
        public int AudioKbps { get; set; } = 128;

        // Ses Gecikmesi (Milisaniye)
        public int AudioDelayMs { get; set; } = 0;

        // --- Çözünürlük & fps ---
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public int Fps { get; set; } = 30;

        // --- Yerel kayıt ---
        public string? RecordFolder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "UniCast");
        public bool EnableLocalRecord { get; set; } = false;

        // --- Sosyal/Platform alanları ---
        public string? YouTubeApiKey { get; set; }
        public string? YouTubeChannelId { get; set; }
        public string? TikTokRoomId { get; set; }
        public string? InstagramUserId { get; set; }
        public string? InstagramSessionId { get; set; }
        public string? FacebookAccessToken { get; set; }
        public string? FacebookPageId { get; set; }
        public string? FacebookLiveVideoId { get; set; }

        // --- Overlay ---
        public bool ShowOverlay { get; set; } = false;
        public int OverlayX { get; set; } = 24;
        public int OverlayY { get; set; } = 24;
        public double OverlayOpacity { get; set; } = 0.85;
        public int OverlayFontSize { get; set; } = 18;
        public string? TikTokSessionCookie { get; set; }

        // --- Profil yönetimi ---
        public List<Profile> Profiles { get; set; } = new()
        {
            Profile.Default()
        };

        public string? SelectedProfileName { get; set; } = Profile.Default().Name;

        public Profile GetSelectedProfile()
        {
            var p = Profiles.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(SelectedProfileName) &&
                string.Equals(x.Name, SelectedProfileName, StringComparison.OrdinalIgnoreCase));

            return p ?? Profile.Default();
        }

        // --- HATA DÜZELTME: Eksik olan Normalize metodu eklendi ---
        public void Normalize()
        {
            // Değerleri mantıklı sınırlara çek (Clamp)
            OverlayOpacity = Math.Clamp(OverlayOpacity, 0.0, 1.0);
            OverlayFontSize = Math.Clamp(OverlayFontSize, 8, 96);

            // Negatif koordinatları engelle
            if (OverlayX < 0) OverlayX = 0;
            if (OverlayY < 0) OverlayY = 0;

            // Ses gecikmesini de negatif olmaktan koru
            if (AudioDelayMs < 0) AudioDelayMs = 0;
        }
    }
}