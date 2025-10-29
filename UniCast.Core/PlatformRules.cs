using System;
using System.Collections.Generic;

namespace UniCast.Core
{
    public static class PlatformRules
    {
        /// <summary>
        /// Platform bazlı üst limitler (genel ve güvenli varsayımlar).
        /// </summary>
        public static (int maxWidth, int maxFps, int maxVideoKbps) GetLimits(Platform p)
        {
            return p switch
            {
                Platform.TikTok => (1280, 30, 2500), // 720p30, ~2.5 Mbps
                Platform.Instagram => (1280, 30, 3500), // 720p30, ~3.5 Mbps
                Platform.Facebook => (1920, 60, 4000), // 1080p60, ~4.0 Mbps
                Platform.YouTube => (1920, 60, 6000), // 1080p60, ~6.0 Mbps
                _ => (1920, 60, 6000), // bilinmeyen: esnek tut
            };
        }

        /// <summary>
        /// Preset verilen platform limitlerini aşıyor mu?
        /// </summary>
        public static bool IsPresetAllowed(Platform p, EncoderProfile preset, out string reason)
        {
            var (maxW, maxFps, maxKbps) = GetLimits(p);
            var warnings = new List<string>();

            if (preset.Width > maxW) warnings.Add($"Çözünürlük {maxW}px üstü");
            if (preset.Fps > maxFps) warnings.Add($"FPS {maxFps} üstü");
            if (preset.VideoKbps > maxKbps) warnings.Add($"Video bitrate {maxKbps} kbps üstü");

            reason = warnings.Count == 0 ? string.Empty : string.Join(", ", warnings);
            return warnings.Count == 0;
        }

        /// <summary>
        /// URL/host metninden platformu tahmin eder.
        /// </summary>
        public static Platform DetectPlatformFromUrl(ReadOnlySpan<char> url)
        {
            var s = url.ToString();
            if (s.IndexOf("tiktok", StringComparison.OrdinalIgnoreCase) >= 0) return Platform.TikTok;
            if (s.IndexOf("instagram", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("ig-live", StringComparison.OrdinalIgnoreCase) >= 0) return Platform.Instagram;
            if (s.IndexOf("facebook", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("fb.", StringComparison.OrdinalIgnoreCase) >= 0) return Platform.Facebook;
            if (s.IndexOf("youtube", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("yt.", StringComparison.OrdinalIgnoreCase) >= 0) return Platform.YouTube;
            return Platform.Unknown;
        }
    }
}
