namespace UniCast.Core.Models
{
    public sealed class Profiles
    {
        public string Name { get; set; } = "Default";
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public int Fps { get; set; } = 30;
        public int VideoBitrateKbps { get; set; } = 6000;
        public int AudioBitrateKbps { get; set; } = 160;
        public string VideoPreset { get; set; } = "veryfast";
        public string AudioCodec { get; set; } = "aac";
        public string VideoCodec { get; set; } = "libx264";

        public static Profile Default() => new Profile();
    }
}
