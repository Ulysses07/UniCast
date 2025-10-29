namespace UniCast.Core.Streaming;

public class StreamTarget
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";   // Örn: rtmp://a.rtmp.youtube.com/live2
    public string Key { get; set; } = "";   // Örn: abc-def-ghi-123
    public bool Enabled { get; set; }       // UI'daki toggle
}
