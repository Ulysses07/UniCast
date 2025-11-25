using System;
using System.Collections.Generic;
using System.Linq;

namespace UniCast.Core.Core
{
    public sealed class Profile
    {
        public string Name { get; set; } = "Default";
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public int Fps { get; set; } = 30;

        // FfmpegArgsBuilder'ın beklediği alanlar
        public int VideoBitrateKbps { get; set; } = 3500;
        public int AudioBitrateKbps { get; set; } = 128;
        public string VideoPreset { get; set; } = "veryfast";
        public string AudioCodec { get; set; } = "aac";
        public string VideoCodec { get; set; } = "libx264";

        // HATA DÜZELTME: Eksik olan özellik eklendi
        public int AudioDelayMs { get; set; } = 0;

        public static Profile Default() => new Profile();

        public static Profile GetByName(string? name, IEnumerable<Profile>? list)
        {
            if (string.IsNullOrWhiteSpace(name)) return list?.FirstOrDefault() ?? Default();
            var l = list ?? Array.Empty<Profile>();
            return l.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Default();
        }
    }
}