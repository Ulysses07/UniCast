namespace UniCast.Core;

public sealed record PlatformConstraint(
    Platform Platform,
    int[] AllowedFps,
    int MaxVideoKbps,
    string[] Notes
);

public static class PlatformConstraints
{
    // Konservatif/emin olduğumuz sınırlar (zamanla güncellenebilir)
    private static readonly Dictionary<Platform, PlatformConstraint> _map = new()
    {
        [Platform.YouTube] = new PlatformConstraint(
            Platform.YouTube,
            AllowedFps: new[] { 30, 60 },
            MaxVideoKbps: 9000, // 1080p60 için güvenli üst sınır
            Notes: new[] { "2 sn GOP önerilir (uygulanıyor)", "Yüksek bitratelerde ağ dalgalanmasına dikkat" }
        ),
        [Platform.Facebook] = new PlatformConstraint(
            Platform.Facebook,
            AllowedFps: new[] { 30, 60 },
            MaxVideoKbps: 6000,
            Notes: new[] { "1080p için pratik üst sınır 6 Mbps", "2 sn GOP önerilir (uygulanıyor)" }
        ),
        [Platform.TikTok] = new PlatformConstraint(
            Platform.TikTok,
            AllowedFps: new[] { 30, 60 },
            MaxVideoKbps: 8000,
            Notes: new[] { "2 sn GOP zorunlu/önerilir (uygulanıyor)", "Aşırı dalgalı bitrate akışa zarar verebilir" }
        ),
        [Platform.Instagram] = new PlatformConstraint(
            Platform.Instagram,
            AllowedFps: new[] { 30 },      // IG Live Producer 30 fps ile en problemsiz
            MaxVideoKbps: 6000,
            Notes: new[] { "Dikey (9:16) tercih edilir, 16:9’ta letterbox olabilir", "2 sn GOP önerilir (uygulanıyor)" }
        )
    };

    public static PlatformConstraint Get(Platform p)
        => _map.TryGetValue(p, out var c) ? c : new PlatformConstraint(p, new[] { 30 }, 6000, Array.Empty<string>());

    /// Uygunsuzlukları döndürür (uyarılar)
    public static IEnumerable<string> Validate(Platform p, EncodePreset preset)
    {
        var c = Get(p);
        if (!c.AllowedFps.Contains(preset.Fps))
            yield return $"{p}: {preset.Fps} fps destek dışı olabilir (izinli: {string.Join('/', c.AllowedFps)})";

        if (preset.VideoKbps > c.MaxVideoKbps)
            yield return $"{p}: Bitrate {preset.VideoKbps} kbps > {c.MaxVideoKbps} kbps (düşürülmeli)";

        foreach (var note in c.Notes)
            yield return $"{p}: {note}";
    }

    /// Platforma uygun otomatik düzeltme
    public static EncodePreset AdjustFor(Platform p, EncodePreset preset)
    {
        var c = Get(p);

        var fps = c.AllowedFps.Contains(preset.Fps) ? preset.Fps : c.AllowedFps.First();
        var vkbps = Math.Min(preset.VideoKbps, c.MaxVideoKbps);

        // Boyutları şimdilik değiştirmiyoruz (16:9 kalır), IG için ileride 1080x1920 preset eklenebilir.
        return preset with { Fps = fps, VideoKbps = vkbps };
    }

    /// Birden çok platform için birleşik düzeltme (en kısıtlayıcıyı uygular)
    public static EncodePreset AdjustForAll(IEnumerable<Platform> platforms, EncodePreset preset)
    {
        var adjusted = preset;
        foreach (var p in platforms)
            adjusted = AdjustFor(p, adjusted);
        return adjusted;
    }

    /// Tüm platformlar için uyarıları derler
    public static string BuildWarnings(IEnumerable<Platform> platforms, EncodePreset preset)
    {
        var lines = new List<string>();
        foreach (var p in platforms)
            lines.AddRange(Validate(p, preset));
        return string.Join(Environment.NewLine, lines.Distinct());
    }
}
