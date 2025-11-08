using System;
using System.Linq;
using System.Text;
using UniCast.Core.Models;
using UniCast.Core.Settings;

namespace UniCast.Encoder
{
    /// <summary>
    /// FFmpeg argümanlarını üretir.
    /// - SettingsData’ya BAĞIMLI DEĞİL.
    /// - Kamera yoksa otomatik ekran yakalamaya düşer.
    /// - Ses yoksa anullsrc kullanır.
    /// </summary>
    public static class FfmpegArgsBuilder
    {
        public static string BuildFfmpegArgs(
            Profile profile,
            System.Collections.Generic.IReadOnlyList<StreamTarget> targets,
            string? videoDevice,
            string? audioDevice,
            bool screenCapture)
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
                // Eğer mutlaka testsrc istiyorsan şunu aç:
                // videoIn = $"-f lavfi -i testsrc=size={w}x{h}:rate={fps}";
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

            // 3) Filtre / encode
            var vf = $"-vf scale={w}:{h},fps={fps}";
            var vEnc = $"-c:v libx264 -preset veryfast -profile:v high -pix_fmt yuv420p -b:v {vKbps}k -maxrate {vKbps}k -bufsize {2 * vKbps}k -g {fps * 2}";
            var aEnc = $"-c:a aac -b:a {aKbps}k -ar 44100 -ac 2";

            // 4) Map ve output’lar
            var sb = new StringBuilder();
            sb.Append($"{videoIn} {audioIn} ");
            sb.Append($"{vf} {vEnc} {aEnc} ");

            // input indexleri: 0=video, 1=audio (yukarıdaki sıraya göre)
            sb.Append("-map 0:v:0 -map 1:a:0 ");

            if (enabledTargets.Count == 1)
            {
                var t = enabledTargets[0];
                var url = BuildRtmpUrl(t);
                sb.Append($"-f flv \"{url}\"");
            }
            else
            {
                // tek encode + tee muxer ile çoklu RTMP
                var outs = enabledTargets.Select(t => $"[f=flv]{EscapeTee(BuildRtmpUrl(t))}");
                sb.Append($"-f tee \"{string.Join("|", outs)}\"");
            }

            return sb.ToString();
        }

        private static string BuildRtmpUrl(StreamTarget t)
        {
            // Url + (opsiyonel) StreamKey birleştir
            // Çoğu servis RTMP endpoint + / + key formatını bekler.
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
