using System.Text;
using UniCast.Core.Models;
using UniCast.Core.Settings;

namespace UniCast.Encoder
{
    public static class FfmpegArgsBuilder
    {
        // Geriye dönük uyumluluk için alias
        public static string BuildArgs(Profile profile, IEnumerable<StreamTarget> targets, SettingsData s,
                                       string? videoDevice = null, string? audioDevice = null, bool? screenCapture = null)
            => BuildFfmpegArgs(profile, targets, s, videoDevice, audioDevice, screenCapture ?? s.UseScreenCapture);

        public static string BuildFfmpegArgs(Profile profile, IEnumerable<StreamTarget> targets, SettingsData s,
                                             string? videoDevice, string? audioDevice, bool screenCapture)
        {
            var sb = new StringBuilder();

            // -------- INPUT --------
            if (screenCapture)
            {
                // gdigrab: tüm Windows ffmpeg build'lerinde var
                sb.Append($" -f gdigrab -framerate {s.Fps} -video_size {s.Width}x{s.Height} -i desktop");
                if (!string.IsNullOrWhiteSpace(audioDevice))
                    sb.Append($" -f dshow -i audio=\"{audioDevice}\"");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(videoDevice))
                    sb.Append($" -f dshow -i video=\"{videoDevice}\"");
                if (!string.IsNullOrWhiteSpace(audioDevice))
                    sb.Append($" -f dshow -i audio=\"{audioDevice}\"");
            }

            // -------- MAP --------
            var hasVideo = screenCapture || !string.IsNullOrWhiteSpace(videoDevice);
            var hasAudio = !string.IsNullOrWhiteSpace(audioDevice);

            if (hasVideo && hasAudio) sb.Append(" -map 0:v:0 -map 1:a:0");
            else if (hasVideo) sb.Append(" -map 0:v:0");
            else if (hasAudio) sb.Append(" -map 0:a:0");

            // -------- ENCODE --------
            if (s.Encoder.Equals("x264", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append($" -c:v libx264 -preset veryfast -b:v {s.VideoKbps}k -pix_fmt yuv420p -r {s.Fps}");
            }
            else // nvenc
            {
                sb.Append($" -c:v h264_nvenc -preset p5 -b:v {s.VideoKbps}k -rc vbr -r {s.Fps}");
            }
            if (hasAudio) sb.Append($" -c:a aac -b:a {s.AudioKbps}k -ar 48000 -ac 2");

            // -------- OUTPUT(S) --------
            int idx = 0;
            foreach (var t in targets.Where(t => t.Enabled))
            {
                var url = t.Url?.Trim();
                if (string.IsNullOrWhiteSpace(url)) continue;

                if (idx > 0) sb.Append(" -copyts -shortest");
                // Varsayılan olarak FLV/RTMP
                sb.Append($" -f flv \"{url}\"");
                idx++;
            }

            return sb.ToString().Trim();
        }
    }
}
