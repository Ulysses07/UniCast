using System;
using System.Linq;
using System.Text;
using UniCast.Core.Core;
using UniCast.Core.Settings;
using UniCast.Core.Streaming;

namespace UniCast.Encoder
{
    /// <summary>
    /// FFmpeg argümanlarını üretir.
    /// - SettingsData’ya BAĞIMLI DEĞİL.
    /// - Kamera yoksa otomatik ekran yakalamaya düşer.
    /// - Ses yoksa anullsrc kullanır.
    /// - İsteğe bağlı overlay (image2pipe) girişiyle filter_complex kurar.
    /// </summary>
    public static class FfmpegArgsBuilder
    {
        public static string BuildFfmpegArgs(
            Profile profile,
            System.Collections.Generic.IReadOnlyList<StreamTarget> targets,
            string? videoDevice,
            string? audioDevice,
            bool screenCapture,
            bool includeOverlay = false,
            int overlayX = 24,
            int overlayY = 24
        )
        {
            if (profile == null) profile = Profile.Default();
            targets ??= Array.Empty<StreamTarget>();

            var enabledTargets = targets.Where(t => t?.Enabled == true && !string.IsNullOrWhiteSpace(t.Url)).ToList();
            if (enabledTargets.Count == 0)
                throw new InvalidOperationException("Etkin RTMP hedefi yok.");

            var w = profile.Width > 0 ? profile.Width : 1280;
            var h = profile.Height > 0 ? profile.Height : 720;
            var fps = profile.Fps > 0 ? profile.Fps : 30;
            var vKbps = profile.VideoBitrateKbps > 0 ? profile.VideoBitrateKbps : 3500;
            var aKbps = profile.AudioBitrateKbps > 0 ? profile.AudioBitrateKbps : 128;

            // 1) Video input
            string videoIn;
            if (screenCapture)
            {
                videoIn = $"-f gdigrab -framerate {fps} -i desktop";
            }
            else if (!string.IsNullOrWhiteSpace(videoDevice))
            {
                videoIn = $"-f dshow -i video=\"{videoDevice}\"";
            }
            else
            {
                // Kamera yok/boş → otomatik ekran yakalama
                videoIn = $"-f gdigrab -framerate {fps} -i desktop";
            }

            // 2) Audio input
            string audioIn;
            if (!string.IsNullOrWhiteSpace(audioDevice))
            {
                audioIn = $"-f dshow -i audio=\"{audioDevice}\"";
            }
            else
            {
                // sessizlik
                audioIn = $"-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100";
            }

            // 3) Overlay (opsiyonel üçüncü input)
            var sb = new StringBuilder();
            sb.Append($"{videoIn} {audioIn} ");
            if (includeOverlay)
            {
                // OverlayPipePublisher PNG (alpha) kare basıyor → image2pipe ile al
                sb.Append("-f image2pipe -r 20 -i \\\\.\\pipe\\unicast_overlay ");
            }

            // 4) Filtre / encode / map
            var vEnc = $"-c:v libx264 -preset veryfast -b:v {vKbps}k -maxrate {vKbps}k -bufsize {2 * vKbps}k -g {fps * 2}";
            var aEnc = $"-c:a aac -b:a {aKbps}k -ar 44100 -ac 2";

            if (includeOverlay)
            {
                // [0:v]=video, [1:a]=audio, [2:v]=overlay
                sb.Append("-filter_complex ");
                sb.Append($"\"[0:v]scale={w}:{h},fps={fps},format=bgra,setsar=1[v0];");
                sb.Append($"[2:v]format=bgra,setsar=1[v2];");
                sb.Append($"[v0][v2]overlay=x={overlayX}:y={overlayY}:format=auto:eval=frame[vout]\" ");
                sb.Append($"{vEnc} {aEnc} ");
                sb.Append("-map [vout] -map 1:a:0 ");
            }
            else
            {
                // Overlay yoksa basit -vf ile
                sb.Append($"-vf scale={w}:{h},fps={fps} {vEnc} {aEnc} ");
                sb.Append("-map 0:v:0 -map 1:a:0 ");
            }

            // 5) Tek hedef vs tee muxer
            if (enabledTargets.Count == 1)
            {
                var t = enabledTargets[0];
                var url = BuildUrl(t);
                sb.Append($"-f flv \"{url}\"");
            }
            else
            {
                var teeParts = enabledTargets.Select(t => $"[f=flv]{EscapeTee(BuildUrl(t))}");
                var teeStr = string.Join("|", teeParts);
                sb.Append($"-f tee \"{teeStr}\"");
            }

            return sb.ToString();
        }

        private static string BuildUrl(StreamTarget t)
        {
            if (!string.IsNullOrWhiteSpace(t.StreamKey))
            {
                var sep = t.Url!.EndsWith("/") ? "" : "/";
                return $"{t.Url}{sep}{t.StreamKey}";
            }
            return t.Url!;
        }

        private static string EscapeTee(string s)
        {
            // tee muxer için | özel karakter, hedeflerde kaçış gerektirir.
            return s.Replace("|", "\\|");
        }
    }
}
