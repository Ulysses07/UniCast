using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UniCast.Core;
using UniCast.Core.Models;
using UniCast.Core.Settings;

namespace UniCast.Encoder
{
    public static class FfmpegArgsBuilder
    {
        public sealed class BuildResult
        {
            public required string Args;
            public required string[] Outputs;
            public required string VideoEncoder;
            public required string AudioEncoder;
        }

        public static BuildResult BuildSingleEncodeMultiRtmp(
            IEnumerable<TargetItem> targets,
            SettingsData settings,
            EncoderProfile profile,
            string? recordFilePath = null)
        {
            var active = targets.Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Url)).ToList();
            if (active.Count == 0) throw new InvalidOperationException("Aktif RTMP hedefi yok.");

            var capped = ApplyCaps(active.Select(t => PlatformRules.DetectByUrl(t.Url))
                                         .Select(PlatformRules.Get), profile);

            var (venc, aenc, vopts) = ChooseVideoEncoder(capped);
            var audioKbps = Math.Min(capped.AudioKbps, 320);
            var gop = Math.Max(1, capped.GopSeconds) * capped.Fps;

            if (string.IsNullOrWhiteSpace(settings.DefaultCamera))
                throw new InvalidOperationException("Kamera seçilmemiş (Ayarlar > Kamera).");
            if (string.IsNullOrWhiteSpace(settings.DefaultMicrophone))
                throw new InvalidOperationException("Mikrofon seçilmemiş (Ayarlar > Mikrofon).");

            var sb = new StringBuilder();
            sb.Append(" -hide_banner -loglevel warning -stats ");
            sb.Append($" -f dshow -rtbufsize 256M -video_size {capped.Width}x{capped.Height} -framerate {capped.Fps} ");
            sb.Append($" -i video=\"{settings.DefaultCamera}\":audio=\"{settings.DefaultMicrophone}\" ");
            sb.Append($" -pix_fmt yuv420p -r {capped.Fps} -s {capped.Width}x{capped.Height} ");
            sb.Append($" -c:v {venc} {vopts} -b:v {capped.VideoKbps}k -maxrate {capped.VideoKbps}k -bufsize {capped.VideoKbps * 2}k -g {gop} -keyint_min {gop} ");
            sb.Append($" -c:a aac -b:a {audioKbps}k -ar 48000 -ac 2 ");

            var teeParts = new List<string>();
            foreach (var t in active)
                teeParts.Add($"[f=flv:onfail=ignore]{t.Url.Trim()}");
            if (!string.IsNullOrWhiteSpace(recordFilePath))
                teeParts.Add($"[f=flv:onfail=ignore]{recordFilePath}");

            sb.Append($" -f tee \"{string.Join("|", teeParts)}\"");

            return new BuildResult
            {
                Args = sb.ToString(),
                Outputs = active.Select(a => a.Url.Trim()).ToArray(),
                VideoEncoder = venc,
                AudioEncoder = "aac"
            };
        }

        private static (string venc, string aenc, string vopts) ChooseVideoEncoder(EncoderProfile p)
        {
            var e = (p.Encoder ?? "auto").Trim().ToLowerInvariant();
            return e switch
            {
                "h264_nvenc" => ("h264_nvenc", "aac", "-preset p4 -rc cbr -tune ll"),
                "h264_qsv" => ("h264_qsv", "aac", "-preset veryfast -global_quality 23"),
                "h264_amf" => ("h264_amf", "aac", "-usage transcoding -quality quality -rc cbr"),
                _ => ("libx264", "aac", "-preset veryfast -profile:v high"),
            };
        }

        private static EncoderProfile ApplyCaps(IEnumerable<PlatformConstraint> constraints, EncoderProfile p)
        {
            var c = constraints.Aggregate(new { W = int.MaxValue, H = int.MaxValue, F = int.MaxValue, V = int.MaxValue, A = int.MaxValue },
                (acc, x) => new {
                    W = Math.Min(acc.W, x.MaxWidth),
                    H = Math.Min(acc.H, x.MaxHeight),
                    F = Math.Min(acc.F, x.MaxFps),
                    V = Math.Min(acc.V, x.MaxVideoKbps),
                    A = Math.Min(acc.A, x.MaxAudioKbps)
                });

            int w = Math.Min(p.Width, c.W), h = Math.Min(p.Height, c.H);
            int f = Math.Min(p.Fps, c.F), vk = Math.Min(p.VideoKbps, c.V), ak = Math.Min(p.AudioKbps, c.A);
            if ((w & 1) == 1) w--; if ((h & 1) == 1) h--; if (w < 2 || h < 2) { w = 1280; h = 720; }

            return new EncoderProfile
            {
                Width = w,
                Height = h,
                Fps = Math.Max(10, f),
                VideoKbps = Math.Max(300, vk),
                AudioKbps = Math.Max(64, ak),
                Encoder = p.Encoder,
                GopSeconds = p.GopSeconds
            };
        }
    }
}
