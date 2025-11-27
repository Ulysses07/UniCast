using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UniCast.Core.Core;
using UniCast.Core.Settings;
using UniCast.Core.Streaming;

namespace UniCast.Encoder
{
    public static class FfmpegArgsBuilder
    {
        public static string BuildFfmpegArgs(
            Profile profile,
            List<StreamTarget> targets,
            string? videoDeviceName,
            string? audioDeviceName,
            bool screenCapture,
            int audioDelayMs,
            string? localRecordPath,
            string? overlayPipeName,
            string? encoderName)
        {
            var sb = new StringBuilder();

            // Encoder Ayarları
            string encoder = string.IsNullOrWhiteSpace(encoderName) || encoderName == "auto" ? "libx264" : encoderName;
            string encParams = "";

            if (encoder.Contains("nvenc")) // NVIDIA
            {
                // p1: En hızlı (Lowest Latency)
                encParams = "-c:v h264_nvenc -preset p1 -tune zerolatency -rc cbr";
            }
            else if (encoder.Contains("amf")) // AMD
            {
                encParams = "-c:v h264_amf -usage ultralowlatency -quality speed";
            }
            else if (encoder.Contains("qsv")) // INTEL
            {
                encParams = "-c:v h264_qsv -preset veryfast";
            }
            else // CPU
            {
                encParams = "-c:v libx264 -preset ultrafast -tune zerolatency";
            }

            // --- 1. GİRİŞLER ---
            sb.Append("-f dshow -rtbufsize 500M -thread_queue_size 2048 ");

            // INPUT 0: VİDEO
            if (screenCapture)
            {
                sb.Clear();
                sb.Append("-rtbufsize 500M -thread_queue_size 2048 ");

                // PERFORMANS GÜNCELLEMESİ: gdigrab yerine ddagrab (DirectX)
                // Bu, ekran yakalamayı GPU üzerinden yapar, CPU'yu rahatlatır.
                sb.Append($"-f ddagrab -framerate {profile.Fps} -i desktop ");
            }
            else if (!string.IsNullOrWhiteSpace(videoDeviceName))
            {
                sb.Append($"-i video=\"{videoDeviceName}\" ");
            }
            else
            {
                sb.Clear();
                sb.Append($"-re -f lavfi -i testsrc=size={profile.Width}x{profile.Height}:rate={profile.Fps} ");
            }

            // INPUT 1: SES
            if (!string.IsNullOrWhiteSpace(audioDeviceName))
            {
                if (audioDelayMs > 0)
                    sb.Append($"-itsoffset {audioDelayMs / 1000.0:0.000} ");

                sb.Append($"-f dshow -i audio=\"{audioDeviceName}\" ");
            }
            else
            {
                sb.Append("-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 ");
            }

            // INPUT 2: OVERLAY
            int overlayIndex = -1;
            if (!string.IsNullOrEmpty(overlayPipeName))
            {
                sb.Append($"-f image2pipe -framerate {profile.Fps} -i \"\\\\.\\pipe\\{overlayPipeName}\" ");
                overlayIndex = 2;
            }

            // --- 2. FİLTRELER ---
            bool hasHorizontal = targets.Any(t => IsHorizontal(MapPlatform(t.Platform)));
            bool hasVertical = targets.Any(t => IsVertical(MapPlatform(t.Platform)));
            if (!string.IsNullOrEmpty(localRecordPath)) hasHorizontal = true;

            bool needsFilter = (overlayIndex != -1) || hasVertical;
            string vMain = "0:v";
            string mapHorizontal = "0:v";
            string mapVertical = "0:v";

            if (needsFilter)
            {
                sb.Append(" -filter_complex \"");

                if (overlayIndex != -1)
                {
                    sb.Append($"[{vMain}][{overlayIndex}:v]overlay=0:0:eof_action=pass[v_overlaid];");
                    vMain = "[v_overlaid]";
                }

                if (hasVertical)
                {
                    if (hasHorizontal)
                    {
                        sb.Append($"{vMain}split=2[v_hor][v_raw_vert];");
                        sb.Append("[v_raw_vert]crop=w=ih*(9/16):h=ih:x=(iw-ow)/2:y=0[v_vert]");
                        mapHorizontal = "[v_hor]";
                        mapVertical = "[v_vert]";
                    }
                    else
                    {
                        sb.Append($"{vMain}crop=w=ih*(9/16):h=ih:x=(iw-ow)/2:y=0[v_vert]");
                        mapVertical = "[v_vert]";
                    }
                }
                else
                {
                    mapHorizontal = vMain;
                }

                if (sb[sb.Length - 1] == ';') sb.Remove(sb.Length - 1, 1);
                sb.Append("\" ");
            }

            // --- 3. ÇIKIŞLAR ---
            int gopSize = profile.Fps * 2;

            foreach (var target in targets)
            {
                Platform p = MapPlatform(target.Platform);
                string videoSource = IsVertical(p) ? mapVertical : mapHorizontal;

                sb.Append($"-map {videoSource} -map 1:a ");
                sb.Append($"{encParams} ");

                sb.Append($"-b:v {profile.VideoBitrateKbps}k -maxrate {profile.VideoBitrateKbps}k -bufsize {profile.VideoBitrateKbps * 2}k ");
                sb.Append($"-g {gopSize} -keyint_min {gopSize} -sc_threshold 0 ");
                sb.Append($"-r {profile.Fps} ");

                sb.Append("-pix_fmt yuv420p ");
                sb.Append($"-c:a aac -b:a {profile.AudioBitrateKbps}k -ar 44100 ");
                sb.Append($"-f flv \"{target.Url}\" ");
            }

            // Yerel Kayıt
            if (!string.IsNullOrEmpty(localRecordPath))
            {
                sb.Append($"-map {mapHorizontal} -map 1:a ");
                sb.Append($"{encParams} ");
                sb.Append($"-r {profile.Fps} ");
                sb.Append("-movflags +faststart ");
                sb.Append($"-f mp4 \"{localRecordPath}\" ");
            }

            return sb.ToString();
        }

        private static Platform MapPlatform(StreamPlatform sp)
        {
            return sp switch
            {
                StreamPlatform.YouTube => Platform.YouTube,
                StreamPlatform.Facebook => Platform.Facebook,
                StreamPlatform.Twitch => Platform.Twitch,
                StreamPlatform.TikTok => Platform.TikTok,
                StreamPlatform.Instagram => Platform.Instagram,
                _ => Platform.Unknown
            };
        }

        private static bool IsVertical(Platform p) => p == Platform.TikTok || p == Platform.Instagram;
        private static bool IsHorizontal(Platform p) => !IsVertical(p);
    }
}