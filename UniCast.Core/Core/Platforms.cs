using System;
using System.Collections.Generic;
using System.Linq;

namespace UniCast.Core
{
    public enum Platform { Unknown, YouTube, Facebook, TikTok, Instagram, Twitch }

    public sealed class PlatformConstraint
    {
        public required int MaxWidth { get; init; }
        public required int MaxHeight { get; init; }
        public required int MaxFps { get; init; }
        public required int MaxVideoKbps { get; init; }
        public required int MaxAudioKbps { get; init; }
        public string[] UrlPrefixes { get; init; } = Array.Empty<string>();
    }

    public static class PlatformRules
    {
        private static readonly Dictionary<Platform, PlatformConstraint> Map = new()
        {
            [Platform.YouTube] = new()
            {
                MaxWidth = 1920,
                MaxHeight = 1080,
                MaxFps = 60,
                MaxVideoKbps = 12000,
                MaxAudioKbps = 320,
                UrlPrefixes = new[] { "rtmp://a.rtmp.youtube.com", "rtmp://x.rtmp.youtube.com", "rtmp://", "rtmps://" }
            },
            [Platform.Facebook] = new()
            {
                MaxWidth = 1920,
                MaxHeight = 1080,
                MaxFps = 60,
                MaxVideoKbps = 6000,
                MaxAudioKbps = 256,
                UrlPrefixes = new[] { "rtmp://live-api-s.facebook.com", "rtmps://" }
            },
            [Platform.TikTok] = new()
            {
                MaxWidth = 1920,
                MaxHeight = 1080,
                MaxFps = 60,
                MaxVideoKbps = 8000,
                MaxAudioKbps = 256,
                UrlPrefixes = new[] { "rtmp://", "rtmps://" }
            },
            [Platform.Instagram] = new()
            {
                MaxWidth = 1080,
                MaxHeight = 1920,
                MaxFps = 30,
                MaxVideoKbps = 6000,
                MaxAudioKbps = 256,
                UrlPrefixes = new[] { "rtmp://", "rtmps://" }
            },
            [Platform.Twitch] = new()
            {
                MaxWidth = 1920,
                MaxHeight = 1080,
                MaxFps = 60,
                MaxVideoKbps = 8500,
                MaxAudioKbps = 320,
                UrlPrefixes = new[] { "rtmp://live", "rtmp://", "rtmps://" }
            },
        };

        public static Platform DetectByUrl(string url)
        {
            var u = (url ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(u)) return Platform.Unknown;
            if (u.Contains("youtube")) return Platform.YouTube;
            if (u.Contains("facebook")) return Platform.Facebook;
            if (u.Contains("tiktok")) return Platform.TikTok;
            if (u.Contains("instagram")) return Platform.Instagram;
            if (u.Contains("twitch")) return Platform.Twitch;
            foreach (var kv in Map) if (kv.Value.UrlPrefixes.Any(p => u.StartsWith(p))) return kv.Key;
            return Platform.Unknown;
        }

        public static PlatformConstraint Get(Platform p)
            => Map.TryGetValue(p, out var c) ? c :
               new PlatformConstraint { MaxWidth = 1920, MaxHeight = 1080, MaxFps = 60, MaxVideoKbps = 6000, MaxAudioKbps = 256 };
    }
}
