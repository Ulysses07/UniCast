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
        // ---- FFmpeg argüman üretimi sonucu ----
        public sealed class BuildResult
        {
            public required string Args { get; init; }
            public required string[] Outputs { get; init; }
            public required string VideoEncoder { get; init; }
            public required string AudioEncoder { get; init; }
            public string Advisory { get; init; } = "";
        }

        // --------------------------------------------------------------------
        // A) OVERLAY'LI: tek encode → çoklu RTMP (+opsiyonel kayıt) + chat overlay
        //     - Kamera/Mikrofon yoksa lavfi fallback (testsrc/anullsrc)
        //     - Overlay input: named pipe üzerinden PNG (image2pipe)
        // --------------------------------------------------------------------
        public static BuildResult BuildSingleEncodeMultiRtmpWithOverlay(
            IEnumerable<TargetItem> targets,
            SettingsData settings,
            EncoderProfile profile,
            string? recordFile,
            string overlayPipeName = "unicast_overlay",
            int overlayX = 20,
            int overlayY = 20)
        {
            var sb = new StringBuilder();

            // --- GİRİŞLER ---
            // Cihaz yoksa lavfi fallback: testsrc (video) + anullsrc (audio)
            bool useLavfiVideo = string.IsNullOrWhiteSpace(settings.DefaultCamera);
            bool useLavfiAudio = string.IsNullOrWhiteSpace(settings.DefaultMicrophone);

            if (useLavfiVideo)
                sb.Append($" -f lavfi -i testsrc=size={profile.Width}x{profile.Height}:rate={profile.Fps} ");
            else
                sb.Append($" -f dshow -rtbufsize 1024M -i video=\"{settings.DefaultCamera}\" ");

            if (useLavfiAudio)
                sb.Append($" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=48000 ");
            else
                sb.Append($" -f dshow -rtbufsize 1024M -i audio=\"{settings.DefaultMicrophone}\" ");

            // 2: Overlay (PNG stream via named pipe)
            sb.Append($" -thread_queue_size 128 -framerate 20 -f image2pipe -vcodec png -i \\\\.\\pipe\\{overlayPipeName} ");

            // --- CODEC/AYARLAR ---
            var vCodec = SelectVideoCodec(settings.Encoder);
            var aCodec = "aac";
            int g = Math.Max(2, profile.GopSeconds) * profile.Fps;

            // [0:v] ana video (lavfi veya dshow), [2:v] overlay PNG
            var vf = $"[0:v][2:v]overlay={overlayX}:{overlayY}:format=auto";
            sb.Append($" -filter_complex \"{vf}\" ");

            sb.Append($" -c:v {vCodec} -b:v {profile.VideoKbps}k -maxrate {profile.VideoKbps}k -bufsize {profile.VideoKbps * 2}k ");
            sb.Append($" -g {g} -r {profile.Fps} -s {profile.Width}x{profile.Height} -pix_fmt yuv420p ");
            sb.Append($" -c:a {aCodec} -b:a {profile.AudioKbps}k -ar 48000 -ac 2 ");

            // --- ÇIKIŞLAR (tee) ---
            var tee = new List<string>();
            foreach (var t in targets)
            {
                var url = t.Url?.Trim();
                if (!string.IsNullOrWhiteSpace(url))
                    tee.Add($"[f=flv]{url}");
            }

            if (!string.IsNullOrWhiteSpace(recordFile))
                tee.Add($"[f=mp4]{recordFile}");

            sb.Append($" -f tee \"{string.Join("|", tee)}\"");

            var advisory = $"Overlay aktif (pipe: \\\\.\\pipe\\{overlayPipeName})";
            if (useLavfiVideo || useLavfiAudio)
            {
                var parts = new List<string>();
                if (useLavfiVideo) parts.Add("video=testsrc");
                if (useLavfiAudio) parts.Add("audio=anullsrc");
                advisory += " | Lavfi fallback: " + string.Join(", ", parts);
            }

            return new BuildResult
            {
                Args = sb.ToString(),
                Outputs = tee.ToArray(),
                VideoEncoder = vCodec,
                AudioEncoder = aCodec,
                Advisory = advisory
            };
        }

        // --------------------------------------------------------------------
        // B) OVERLAY'SİZ: tek encode → çoklu RTMP (+opsiyonel kayıt)
        //     - Kamera/Mikrofon yoksa lavfi fallback (testsrc/anullsrc)
        // --------------------------------------------------------------------
        public static BuildResult BuildSingleEncodeMultiRtmp(
            IEnumerable<TargetItem> targets,
            SettingsData settings,
            EncoderProfile profile,
            string? recordFile)
        {
            var sb = new StringBuilder();

            // --- GİRİŞLER (lavfi fallback dahil) ---
            bool useLavfiVideo = string.IsNullOrWhiteSpace(settings.DefaultCamera);
            bool useLavfiAudio = string.IsNullOrWhiteSpace(settings.DefaultMicrophone);

            if (useLavfiVideo)
                sb.Append($" -f lavfi -i testsrc=size={profile.Width}x{profile.Height}:rate={profile.Fps} ");
            else
                sb.Append($" -f dshow -rtbufsize 1024M -i video=\"{settings.DefaultCamera}\" ");

            if (useLavfiAudio)
                sb.Append($" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=48000 ");
            else
                sb.Append($" -f dshow -rtbufsize 1024M -i audio=\"{settings.DefaultMicrophone}\" ");

            // --- CODEC/AYARLAR ---
            var vCodec = SelectVideoCodec(settings.Encoder);
            var aCodec = "aac";
            int g = Math.Max(2, profile.GopSeconds) * profile.Fps;

            sb.Append($" -map 0:v:0 -map 1:a:0 "); // video=0, audio=1 girişlerinden

            sb.Append($" -c:v {vCodec} -b:v {profile.VideoKbps}k -maxrate {profile.VideoKbps}k -bufsize {profile.VideoKbps * 2}k ");
            sb.Append($" -g {g} -r {profile.Fps} -s {profile.Width}x{profile.Height} -pix_fmt yuv420p ");
            sb.Append($" -c:a {aCodec} -b:a {profile.AudioKbps}k -ar 48000 -ac 2 ");

            // --- ÇIKIŞLAR (tee) ---
            var tee = new List<string>();
            foreach (var t in targets)
            {
                var url = t.Url?.Trim();
                if (!string.IsNullOrWhiteSpace(url))
                    tee.Add($"[f=flv]{url}");
            }

            if (!string.IsNullOrWhiteSpace(recordFile))
                tee.Add($"[f=mp4]{recordFile}");

            sb.Append($" -f tee \"{string.Join("|", tee)}\"");

            var advisory = "Overlay kapalı";
            if (useLavfiVideo || useLavfiAudio)
            {
                var parts = new List<string>();
                if (useLavfiVideo) parts.Add("video=testsrc");
                if (useLavfiAudio) parts.Add("audio=anullsrc");
                advisory += " | Lavfi fallback: " + string.Join(", ", parts);
            }

            return new BuildResult
            {
                Args = sb.ToString(),
                Outputs = tee.ToArray(),
                VideoEncoder = vCodec,
                AudioEncoder = aCodec,
                Advisory = advisory
            };
        }

        // --------------------------------------------------------------------
        // Yardımcılar
        // --------------------------------------------------------------------
        /// <summary>
        /// Kullanıcı Encoder tercihine göre ffmpeg -c:v değeri.
        /// Auto durumda basitçe libx264 döndürür (istersen donanım probe ekleyebilirsin).
        /// </summary>
        private static string SelectVideoCodec(string? pref)
        {
            var p = (pref ?? "auto").Trim().ToLowerInvariant();
            return p switch
            {
                "h264_nvenc" => "h264_nvenc",
                "nvenc" => "h264_nvenc",
                "h264_qsv" => "h264_qsv",
                "qsv" => "h264_qsv",
                "h264_amf" => "h264_amf",
                "amf" => "h264_amf",
                "x264" => "libx264",
                "libx264" => "libx264",
                "auto" => "libx264",
                _ => "libx264"
            };
        }

        // (İsteğe bağlı) Platform kısıtlarını ve encoder notlarını advisory içine toplayan yardımcılar:
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

            // 2'ye bölünebilirlik (yuv420p gereği)
            if ((w & 1) == 1) w--;
            if ((h & 1) == 1) h--;
            if (w < 2 || h < 2) { w = 1280; h = 720; }

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
