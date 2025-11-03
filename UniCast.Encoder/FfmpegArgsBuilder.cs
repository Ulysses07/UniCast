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

        private static string SelectVideoCodec(string desired)
        {
            desired = (desired ?? "auto").Trim().ToLowerInvariant();
            if (desired is "h264_nvenc" or "h264_qsv" or "h264_amf" or "libx264") return desired;
            var probe = EncoderProbe.Run();
            if (probe.HasNvenc) return "h264_nvenc";
            if (probe.HasQsv) return "h264_qsv";
            if (probe.HasAmf) return "h264_amf";
            return "libx264";
        }

        public static BuildResult BuildSingleEncodeMultiRtmpWithOverlay(
            IEnumerable<TargetItem> targets,
            SettingsData settings,
            EncoderProfile profile,
            string? recordFile,
            string overlayPipeName = "unicast_overlay",
            int overlayX = 20,
            int overlayY = 20)
        {
            var outs = new List<string>();
            foreach (var t in targets)
            {
                var url = t.Url?.Trim();
                if (!string.IsNullOrWhiteSpace(url)) outs.Add($"[f=flv]{url}");
            }
            if (!string.IsNullOrWhiteSpace(recordFile)) outs.Add($"[f=mp4]{recordFile}");
            if (outs.Count == 0) outs.Add("[f=flv]rtmp://127.0.0.1/live/test");

            var sb = new StringBuilder();

            // Video input
            if (!string.IsNullOrWhiteSpace(settings.DefaultCamera))
            {
                sb.Append($" -f dshow -rtbufsize 1024M -i video=\"{settings.DefaultCamera}\" ");
            }
            else
            {
                sb.Append($" -f lavfi -i testsrc=size={profile.Width}x{profile.Height}:rate={profile.Fps} ");
            }

            // Audio input
            if (!string.IsNullOrWhiteSpace(settings.DefaultMicrophone))
            {
                sb.Append($" -f dshow -rtbufsize 1024M -i audio=\"{settings.DefaultMicrophone}\" ");
            }
            else
            {
                sb.Append(" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=48000 ");
            }

            // Overlay input (PNG akışı: named pipe)
            sb.Append($" -thread_queue_size 256 -f image2pipe -r 20 -i \\\\.\\pipe\\{overlayPipeName} ");

            var vCodec = SelectVideoCodec(settings.Encoder);
            var aCodec = "aac";
            int g = Math.Max(2, profile.GopSeconds) * profile.Fps;

            // [0:v]=ana video, [2:v]=overlay PNG
            var vf = $"[0:v][2:v]overlay={overlayX}:{overlayY}:format=auto";
            sb.Append($" -filter_complex \"{vf}\" ");

            sb.Append($" -c:v {vCodec} -b:v {profile.VideoKbps}k -maxrate {profile.VideoKbps}k -bufsize {profile.VideoKbps * 2}k ");
            sb.Append($" -g {g} -r {profile.Fps} -s {profile.Width}x{profile.Height} -pix_fmt yuv420p ");
            sb.Append($" -c:a {aCodec} -b:a {profile.AudioKbps}k -ar 48000 -ac 2 ");
            sb.Append($" -f tee \"{string.Join("|", outs)}\"");

            return new BuildResult
            {
                Args = sb.ToString(),
                Outputs = outs.ToArray(),
                VideoEncoder = vCodec,
                AudioEncoder = aCodec,
                Advisory = $"Overlay aktif (pipe: \\\\.\\pipe\\{overlayPipeName})"
            };
        }
    }
}
