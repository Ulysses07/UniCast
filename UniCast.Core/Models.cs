using System;
using System.Collections.Generic;

namespace UniCast.Core
{
    public enum Platform
    {
        YouTube,
        Facebook,
        TikTok,
        Instagram,
        Unknown
    }

    // Encoder ölçümleri
    public sealed class EncoderMetrics
    {
        public double Fps { get; init; }
        public double DropPercent { get; init; }
        public int BitrateKbps { get; init; }
        public DateTimeOffset At { get; init; }
        public double VideoKbps { get; init; }
        public double AudioKbps { get; init; }

        public EncoderMetrics() { }

        public EncoderMetrics(double fps, double dropPercent, int bitrateKbps,
                              DateTimeOffset at, double videoKbps, double audioKbps)
        {
            Fps = fps;
            DropPercent = dropPercent;
            BitrateKbps = bitrateKbps;
            At = at;
            VideoKbps = videoKbps;
            AudioKbps = audioKbps;
        }
    }

    // Encoder profili
    public sealed class EncoderProfile
    {
        public string Name { get; }
        public int Width { get; }
        public int Height { get; }
        public int Fps { get; }
        public int VideoKbps { get; }
        public int AudioKbps { get; }

        public EncoderProfile(string name, int width, int height, int fps, int videoKbps, int audioKbps)
        {
            Name = name;
            Width = width;
            Height = height;
            Fps = fps;
            VideoKbps = videoKbps;
            AudioKbps = audioKbps;
        }
    }

    // Basit presetler
    public static class Preset
    {
        public static EncoderProfile Safe720p30 =>
            new EncoderProfile("Safe 720p30", 1280, 720, 30, 3500, 128);

        public static EncoderProfile Safe1080p30 =>
            new EncoderProfile("Safe 1080p30", 1920, 1080, 30, 6000, 160);

        public static EncoderProfile Safe1080p60 =>
            new EncoderProfile("Safe 1080p60", 1920, 1080, 60, 9000, 160);
    }

    // Encoder servisi kontratı
    public interface IEncoderService : IDisposable
    {
        bool IsRunning { get; }
        event Action<EncoderMetrics>? OnMetrics;

        // Kısa imza
        System.Threading.Tasks.Task StartAsync(EncoderProfile profile,
                                               IReadOnlyList<string> rtmpTargets,
                                               System.Threading.CancellationToken ct);

        // Geniş imza (bazı yerlerde bu çağrılıyor)
        System.Threading.Tasks.Task StartAsync(EncoderProfile profile,
                                               IReadOnlyList<string> rtmpTargets,
                                               string? captureVideo,
                                               string? captureAudio,
                                               System.Threading.CancellationToken ct);

        System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken ct);
    }
}
