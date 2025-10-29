using System.Text;
using UniCast.Core;

namespace UniCast.Encoder;

public static class FfmpegArgsBuilder
{
    public static string BuildForPreset(
        EncodePreset p,
        IReadOnlyList<string> rtmpTargets,
        bool nvencPreferred = true,
        string inputSource = "desktop",
        bool realtime = true)
    {
        if (rtmpTargets == null || rtmpTargets.Count == 0)
            throw new ArgumentException("En az bir RTMP/RTMPS hedef gerekli.", nameof(rtmpTargets));

        var sb = new StringBuilder();

        // input (Windows masaüstü + varsayılan sistem ses örneği)
        if (inputSource.Equals("desktop", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append($"-f gdigrab -framerate {p.Fps} -video_size {p.Width}x{p.Height} -i desktop ");
            // sistem sesin yoksa bu satırı kaldır veya doğru capture cihaz adını yaz
            sb.Append("-f dshow -i audio=\"virtual-audio-capturer\" ");
        }
        else
        {
            sb.Append($"-f lavfi -i testsrc=size={p.Width}x{p.Height}:rate={p.Fps} ");
            sb.Append("-f lavfi -i sine=frequency=1000 ");
        }

        if (!realtime)
            sb.Insert(0, "-re ");

        var vEnc = nvencPreferred
            ? "-c:v h264_nvenc -preset p1 -rc cbr_hq"
            : "-c:v libx264 -preset veryfast -x264-params nal-hrd=cbr";

        sb.Append("-pix_fmt yuv420p ");
        sb.Append($"{vEnc} -b:v {p.VideoKbps}k -maxrate {p.VideoKbps}k -bufsize {p.VideoKbps * 2}k ");
        sb.Append($"-g {p.Fps * 2} -bf 2 -sc_threshold 0 ");
        sb.Append($"-c:a aac -b:a {p.AudioKbps}k -ar 48000 -ac 2 ");

        if (rtmpTargets.Count == 1)
        {
            sb.Append($"-f flv {rtmpTargets[0]}");
        }
        else
        {
            // Hata durumunda devam edebilmek için onfail=ignore ekliyoruz.
            // FIFO, ani dalgalanmalarda queue etkisi sağlar.
            var tee = string.Join("|", rtmpTargets
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => $"[f=flv:onfail=ignore:use_fifo=1]{u.Trim()}"));

            sb.Append($"-f tee \"{tee}\"");
        }

        return sb.ToString();
    }

    public static string BuildTeeArgs(
        string videoInput, EncoderProfile p, IReadOnlyList<string> rtmpTargets, bool nvencPreferred = true)
    {
        var preset = new EncodePreset(p.Name, p.Width, p.Height, p.Fps, p.VideoKbps, p.AudioKbps);
        return BuildForPreset(preset, rtmpTargets, nvencPreferred, inputSource: videoInput, realtime: true);
    }
}
