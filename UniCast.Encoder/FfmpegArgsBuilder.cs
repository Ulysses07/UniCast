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
            public string Advisory { get; init; } = "";
        }

        public static BuildResult BuildSingleEncodeMultiRtmp(
            IEnumerable<TargetItem> targets,
            SettingsData settings,
            EncoderProfile profile,
            string? recordFilePath = null)
        {
            var active = targets.Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Url)).ToList();
            if (active.Count == 0) throw new InvalidOperationException("Aktif RTMP/RTMPS hedefi yok.");

            // 1) Platform kısıtlarını uygula ve advisory metni oluştur
            var requested = profile;
            var caps = active.Select(t => PlatformRules.DetectByUrl(t.Url))
                             .Select(PlatformRules.Get);
            var capped = ApplyCaps(caps, requested, out string capNote);

            // 2) Donanım encoder auto-detect (settings.Encoder == "auto")
            var (venc, aenc, vopts, encNote) = ChooseVideoEncoderAuto(capped, settings);

            // 3) Kaynak doğrulamaları
            if (string.IsNullOrWhiteSpace(settings.DefaultCamera))
                throw new InvalidOperationException("Kamera seçilmemiş (Ayarlar > Kamera).");
            if (string.IsNullOrWhiteSpace(settings.DefaultMicrophone))
                throw new InvalidOperationException("Mikrofon seçilmemiş (Ayarlar > Mikrofon).");

            var gop = Math.Max(1, capped.GopSeconds) * capped.Fps;
            var audioKbps = Math.Min(capped.AudioKbps, 320);

            // 4) Argümanlar
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

            var advisory = BuildAdvisoryText(requested, capped, encNote, capNote);

            return new BuildResult
            {
                Args = sb.ToString(),
                Outputs = active.Select(a => a.Url.Trim()).ToArray(),
                VideoEncoder = venc,
                AudioEncoder = "aac",
                Advisory = advisory
            };
        }

        private static string BuildAdvisoryText(EncoderProfile req, EncoderProfile eff, string encNote, string capNote)
        {
            var diffs = new List<string>();
            if (eff.Width != req.Width || eff.Height != req.Height)
                diffs.Add($"Çözünürlük {req.Width}x{req.Height} → {eff.Width}x{eff.Height}");
            if (eff.Fps != req.Fps)
                diffs.Add($"FPS {req.Fps} → {eff.Fps}");
            if (eff.VideoKbps != req.VideoKbps)
                diffs.Add($"Video bitrate {req.VideoKbps} → {eff.VideoKbps} kbps");
            if (eff.AudioKbps != req.AudioKbps)
                diffs.Add($"Audio bitrate {req.AudioKbps} → {eff.AudioKbps} kbps");

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(encNote))
                sb.Append(encNote.Trim());
            if (diffs.Count > 0)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append("Platform kısıtları uygulandı: ");
                sb.Append(string.Join(", ", diffs));
            }
            if (!string.IsNullOrWhiteSpace(capNote))
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(capNote.Trim());
            }
            return sb.ToString();
        }

        private static (string venc, string aenc, string vopts, string encNote)
            ChooseVideoEncoderAuto(EncoderProfile p, SettingsData settings)
        {
            var desired = (settings.Encoder ?? "auto").Trim().ToLowerInvariant();

            // Kullanıcı spesifik encoder yazmışsa doğrudan onu kullan
            if (desired is "h264_nvenc" or "h264_qsv" or "h264_amf" or "libx264")
            {
                return desired switch
                {
                    "h264_nvenc" => ("h264_nvenc", "aac", "-preset p4 -rc cbr -tune ll", "Donanım encoder: NVENC."),
                    "h264_qsv" => ("h264_qsv", "aac", "-preset veryfast -global_quality 23", "Donanım encoder: Intel QSV."),
                    "h264_amf" => ("h264_amf", "aac", "-usage transcoding -quality quality -rc cbr", "Donanım encoder: AMD AMF."),
                    _ => ("libx264", "aac", "-preset veryfast -profile:v high", "Yazılım encoder: libx264.")
                };
            }

            // Auto: nvenc > qsv > amf > x264
            var probe = EncoderProbe.Run();
            if (probe.HasNvenc) return ("h264_nvenc", "aac", "-preset p4 -rc cbr -tune ll", "Donanım encoder otomatik: NVENC seçildi.");
            if (probe.HasQsv) return ("h264_qsv", "aac", "-preset veryfast -global_quality 23", "Donanım encoder otomatik: Intel QSV seçildi.");
            if (probe.HasAmf) return ("h264_amf", "aac", "-usage transcoding -quality quality -rc cbr", "Donanım encoder otomatik: AMD AMF seçildi.");
            return ("libx264", "aac", "-preset veryfast -profile:v high", "Donanım encoder bulunamadı: libx264 kullanılıyor.");
        }

        private static EncoderProfile ApplyCaps(IEnumerable<PlatformConstraint> constraints, EncoderProfile p, out string capNote)
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

            // 2'ye bölünebilirlik
            if ((w & 1) == 1) w--; if ((h & 1) == 1) h--; if (w < 2 || h < 2) { w = 1280; h = 720; }

            capNote = $"Maks: {c.W}x{c.H}@{c.F}fps, {c.V}kbps video / {c.A}kbps audio.";
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
