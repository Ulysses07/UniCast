namespace UniCast.Core;

public enum QualityProfile
{
    Safe720p30,   // 1280x720 @ 30fps ~3500 kbps
    Std720p60,    // 1280x720 @ 60fps ~4500 kbps
    Pro1080p30,   // 1920x1080 @ 30fps ~6000 kbps
    Pro1080p60    // 1920x1080 @ 60fps ~8000 kbps
}
public sealed record EncodePreset(
    string Name,
    int Width,
    int Height,
    int Fps,
    int VideoKbps,
    int AudioKbps = 128
);

public static class PresetCatalog
{
    // Platform-özel kısıtlar gerekiyorsa ileride burada düzenleriz.
    public static EncodePreset Get(QualityProfile q) => q switch
    {
        QualityProfile.Safe720p30 => new EncodePreset("Safe 720p30", 1280, 720, 30, 3500, 128),
        QualityProfile.Std720p60 => new EncodePreset("Standard 720p60", 1280, 720, 60, 4500, 128),
        QualityProfile.Pro1080p30 => new EncodePreset("Pro 1080p30", 1920, 1080, 30, 6000, 160),
        QualityProfile.Pro1080p60 => new EncodePreset("Pro 1080p60", 1920, 1080, 60, 8000, 160),
        _ => new EncodePreset("Safe 720p30", 1280, 720, 30, 3500, 128)
    };
}
