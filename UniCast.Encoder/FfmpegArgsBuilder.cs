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
            string? localRecordPath)
        {
            var sb = new StringBuilder();

            // --- Giriş Ayarları ---
            sb.Append("-f dshow -rtbufsize 100M -thread_queue_size 1024 ");

            // GİRİŞ 0: VİDEO
            if (screenCapture)
            {
                sb.Clear();
                sb.Append("-rtbufsize 100M -thread_queue_size 1024 ");
                sb.Append("-f gdigrab -framerate 30 -i desktop ");
            }
            else if (!string.IsNullOrWhiteSpace(videoDeviceName))
            {
                sb.Append($"-i video=\"{videoDeviceName}\" ");
            }
            else
            {
                sb.Clear();
                sb.Append("-f lavfi -i testsrc=size=1280x720:rate=30 ");
            }

            // GİRİŞ 1: SES
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

            // --- Filtreler (Smart Crop) ---
            bool hasHorizontal = targets.Any(t => IsHorizontal(MapPlatform(t.Platform)));
            bool hasVertical = targets.Any(t => IsVertical(MapPlatform(t.Platform)));

            // Kayıt varsa yatay çıkış zorunlu
            if (!string.IsNullOrEmpty(localRecordPath)) hasHorizontal = true;

            string mapHorizontal = "0:v";
            string mapVertical = "0:v";

            if (hasVertical)
            {
                sb.Append(" -filter_complex \"");

                if (hasHorizontal)
                {
                    // Hem Yatay Hem Dikey
                    sb.Append("[0:v]split=2[v_hor][v_raw_vert];");
                    sb.Append("[v_raw_vert]crop=w=ih*(9/16):h=ih:x=(iw-ow)/2:y=0[v_vert]");
                    mapHorizontal = "[v_hor]";
                    mapVertical = "[v_vert]";
                }
                else
                {
                    // Sadece Dikey
                    sb.Append("[0:v]crop=w=ih*(9/16):h=ih:x=(iw-ow)/2:y=0[v_vert]");
                    mapVertical = "[v_vert]";
                }
                sb.Append("\" ");
            }

            // --- Çıkışlar ---
            foreach (var target in targets)
            {
                Platform p = MapPlatform(target.Platform);
                string videoSource = IsVertical(p) ? mapVertical : mapHorizontal;

                sb.Append($"-map {videoSource} -map 1:a ");
                sb.Append("-c:v libx264 -preset ultrafast -tune zerolatency ");
                sb.Append("-b:v 2500k -maxrate 2500k -bufsize 5000k ");
                sb.Append("-pix_fmt yuv420p ");
                sb.Append("-c:a aac -b:a 128k -ar 44100 ");
                sb.Append($"-f flv \"{target.Url}\" ");
            }

            // --- Yerel Kayıt ---
            if (!string.IsNullOrEmpty(localRecordPath))
            {
                sb.Append($"-map {mapHorizontal} -map 1:a ");
                sb.Append("-c:v libx264 -preset ultrafast -crf 23 ");
                sb.Append("-c:a aac -b:a 192k ");
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