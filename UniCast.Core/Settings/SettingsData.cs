using System;
using System.IO;

namespace UniCast.Core.Settings
{
    public sealed class SettingsData
    {
        public string DefaultCamera { get; set; } = "";
        public string DefaultMicrophone { get; set; } = "";

        public string Encoder { get; set; } = "auto";
        public int VideoKbps { get; set; } = 3500;
        public int AudioKbps { get; set; } = 160;
        public int Fps { get; set; } = 30;

        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;

        public bool EnableLocalRecord { get; set; } = false;
        public string RecordFolder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "UniCast");
        public bool ShowOverlay { get; set; } = false;
        public double OverlayX { get; set; } = 0;
        public double OverlayY { get; set; } = 0;
        public double OverlayOpacity { get; set; } = 0.9;
        public double OverlayFontSize { get; set; } = 18;

        // YouTube
        public string YouTubeApiKey { get; set; } = "";         // Secret - DPAPI ile saklanacak
        public string YouTubeChannelId { get; set; } = "";

        // TikTok
        public string TikTokSessionCookie { get; set; } = "";   // Secret - DPAPI ile saklanacak
        public string TikTokRoomId { get; set; } = "";

        public string InstagramUserId { get; set; } = "";   // plain
        public string InstagramSessionId { get; set; } = ""; // plain (DPAPI ile şifrelenerek kaydedilecek)
        public string FacebookPageId { get; set; } = "";       // Sayfa ID (numerik)
        public string FacebookLiveVideoId { get; set; } = "";   // Opsiyonel: canlı video ID'sini doğrudan ver
        public string FacebookAccessToken { get; set; } = "";   // Page Access Token (encrypted kaydedilecek)
    }
}
