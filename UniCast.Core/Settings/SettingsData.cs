using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniCast.Core.Core; // Platform enum vb. için gerekebilir, yoksa kaldırılabilir
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
        public string? TikTokRoomId { get; set; }
        public string? InstagramUserId { get; set; }
        public string? InstagramSessionId { get; set; }
        public string? FacebookAccessToken { get; set; }
        public string? FacebookPageId { get; set; }
        public string? FacebookLiveVideoId { get; set; }
        public string? TikTokSessionCookie { get; set; }

        // --- Overlay Ayarları ---
        public bool ShowOverlay { get; set; } = false;
        public int OverlayX { get; set; } = 24;
        public int OverlayY { get; set; } = 24;
        public double OverlayWidth { get; set; } = 300; // Boyutlandırma için
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
    }
}