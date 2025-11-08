using System;
using System.IO;
using System.Text.Json;
using UniCast.Core.Settings;
using UniCast.App.Security;

namespace UniCast.App.Services
{
    public static class SettingsStore
    {
        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UniCast");

        private static readonly string FilePath = Path.Combine(Dir, "settings.json");

        private sealed class PersistModel
        {
            // Plain alanlar
            public bool ShowOverlay { get; set; }
            public int OverlayX { get; set; }
            public int OverlayY { get; set; }
            public double OverlayOpacity { get; set; }
            public int OverlayFontSize { get; set; }

            public string YouTubeChannelId { get; set; } = "";
            public string TikTokRoomId { get; set; } = "";

            // Encrypted alanlar (base64)
            public string YouTubeApiKeyEnc { get; set; } = "";
            public string TikTokSessionCookieEnc { get; set; } = "";
            public string FacebookPageId { get; set; } = "";
            public string FacebookLiveVideoId { get; set; } = "";
            public string FacebookAccessTokenEnc { get; set; } = ""; // encrypted

            // Eski alanlarınız buradaysa ekleyin (VideoKbps, Fps, Encoder, DefaultCamera/Mic vs.)
            public string Encoder { get; set; } = "auto";
            public int VideoKbps { get; set; } = 3500;
            public int AudioKbps { get; set; } = 160;
            public int Fps { get; set; } = 30;
            public int Width { get; set; } = 1280;
            public int Height { get; set; } = 720;
            public string DefaultCamera { get; set; } = "";
            public string DefaultMicrophone { get; set; } = "";
            public string RecordFolder { get; set; } = "";
            public bool EnableLocalRecord { get; set; } = false;

            // Instagram
            public string InstagramUserId { get; set; } = "";      // plain
            public string InstagramSessionIdEnc { get; set; } = ""; // encrypted (base64)
        }

        public static SettingsData Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new SettingsData();

                var json = File.ReadAllText(FilePath);
                var p = JsonSerializer.Deserialize<PersistModel>(json) ?? new PersistModel();

                var s = new SettingsData
                {
                    // Chat & Overlay
                    ShowOverlay = p.ShowOverlay,
                    OverlayX = p.OverlayX,
                    OverlayY = p.OverlayY,
                    OverlayOpacity = p.OverlayOpacity,
                    OverlayFontSize = p.OverlayFontSize,

                    YouTubeChannelId = p.YouTubeChannelId ?? "",
                    TikTokRoomId = p.TikTokRoomId ?? "",

                    // Secrets (unprotect) – null gelebilirse "" yap
                    YouTubeApiKey = SecretStore.Unprotect(p.YouTubeApiKeyEnc) ?? "",
                    TikTokSessionCookie = SecretStore.Unprotect(p.TikTokSessionCookieEnc) ?? "",
                    FacebookPageId = p.FacebookPageId ?? "",
                    FacebookLiveVideoId = p.FacebookLiveVideoId ?? "",
                    FacebookAccessToken = SecretStore.Unprotect(p.FacebookAccessTokenEnc) ?? "",

                    // Encoding / General
                    Encoder = p.Encoder ?? "auto",
                    VideoKbps = p.VideoKbps,
                    AudioKbps = p.AudioKbps,
                    Fps = p.Fps,
                    Width = p.Width,
                    Height = p.Height,
                    DefaultCamera = p.DefaultCamera ?? "",
                    DefaultMicrophone = p.DefaultMicrophone ?? "",
                    RecordFolder = p.RecordFolder ?? "",
                    EnableLocalRecord = p.EnableLocalRecord,

                    // Instagram
                    InstagramUserId = p.InstagramUserId ?? "",
                    InstagramSessionId = SecretStore.Unprotect(p.InstagramSessionIdEnc) ?? ""
                };

                // Yükleme sonrası güvenli aralıklar
                s.Normalize();

                return s;
            }
            catch
            {
                return new SettingsData();
            }
        }

        public static void Save(SettingsData s)
        {
            Directory.CreateDirectory(Dir);

            var p = new PersistModel
            {
                // Chat & Overlay
                ShowOverlay = s.ShowOverlay,
                OverlayX = s.OverlayX,
                OverlayY = s.OverlayY,
                OverlayOpacity = s.OverlayOpacity,
                OverlayFontSize = s.OverlayFontSize,

                YouTubeChannelId = s.YouTubeChannelId ?? "",
                TikTokRoomId = s.TikTokRoomId ?? "",

                // Secrets (protect) – null ise "" ver ki CS8604 çıkmasın
                YouTubeApiKeyEnc = SecretStore.Protect(s.YouTubeApiKey ?? ""),
                TikTokSessionCookieEnc = SecretStore.Protect(s.TikTokSessionCookie ?? ""),
                InstagramUserId = s.InstagramUserId ?? "",
                InstagramSessionIdEnc = SecretStore.Protect(s.InstagramSessionId ?? ""),
                FacebookPageId = s.FacebookPageId ?? "",
                FacebookLiveVideoId = s.FacebookLiveVideoId ?? "",
                FacebookAccessTokenEnc = SecretStore.Protect(s.FacebookAccessToken ?? ""),

                // Encoding / General
                Encoder = s.Encoder ?? "auto",
                VideoKbps = s.VideoKbps,
                AudioKbps = s.AudioKbps,
                Fps = s.Fps,
                Width = s.Width,
                Height = s.Height,
                DefaultCamera = s.DefaultCamera ?? "",
                DefaultMicrophone = s.DefaultMicrophone ?? "",
                RecordFolder = s.RecordFolder ?? "",
                EnableLocalRecord = s.EnableLocalRecord
            };

            var json = JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
    }
}
