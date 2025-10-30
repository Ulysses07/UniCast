namespace UniCast.Core
{
    public sealed class EncoderProfile
    {
        public int Width { get; init; } = 1280;
        public int Height { get; init; } = 720;
        public int Fps { get; init; } = 30;
        public int VideoKbps { get; init; } = 3500;
        public int AudioKbps { get; init; } = 160;
        public string Encoder { get; init; } = "auto";
        public int GopSeconds { get; init; } = 2;
    }
}
