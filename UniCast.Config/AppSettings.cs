using UniCast.Core;

namespace UniCast.Config;

public sealed class AppSettings
{
    public string YouTubeRtmp { get; set; } = "";
    public string FacebookRtmp { get; set; } = "";
    public string TikTokRtmp { get; set; } = "";
    public string InstagramRtmp { get; set; } = "";
    public QualityProfile SelectedQuality { get; set; } = QualityProfile.Safe720p30;
    public string SelectedPlatform { get; set; } = "YouTube";
}
