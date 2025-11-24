namespace UniCast.Core.Streaming
{
    public enum StreamPlatform { Custom, YouTube, Facebook, Twitch }

    public sealed class StreamTarget
    {
        public StreamPlatform Platform { get; set; } = StreamPlatform.Custom;
        public string? DisplayName { get; set; } // UI’da gösterilecek ad
        public string? Url { get; set; }         // rtmp(s)://.../app
        public string? StreamKey { get; set; }   // publish key
        public bool Enabled { get; set; } = true;
    }
}
