namespace UniCast.Core.Streaming
{
    // GÜNCELLEME: TikTok ve Instagram eklendi
    public enum StreamPlatform
    {
        Custom,
        YouTube,
        Facebook,
        Twitch,
        TikTok,
        Instagram
    }

    public sealed class StreamTarget
    {
        public StreamPlatform Platform { get; set; } = StreamPlatform.Custom;
        public string? DisplayName { get; set; }
        public string? Url { get; set; }
        public string? StreamKey { get; set; }
        public bool Enabled { get; set; } = true;
    }
}