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
            int audioDelayMs) // YENİ PARAMETRE
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

            // GİRİŞ 1: SES (Gecikme Ayarlı)
            if (!string.IsNullOrWhiteSpace(audioDeviceName))
            {
                // Ses Gecikmesi Varsa Ekler
                if (audioDelayMs > 0)
                {
                    // Milisaniyeyi saniyeye çevir (örn: 200ms -> 0.200s)
                    sb.Append($"-itsoffset {audioDelayMs / 1000.0:0.000} ");
                }

                // -f dshow burada tekrar yazılmalı çünkü yukarıda screenCapture ile silinmiş olabilir
                // veya -itsoffset'ten sonra yeni giriş başlıyor.
                sb.Append($"-f dshow -i audio=\"{audioDeviceName}\" ");
            }
            else
            {
                // Ses yoksa sessizlik üret (Input 1)
                sb.Append("-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 ");
            }

            // --- Filtreler (Smart Crop) ---
            bool hasHorizontal = targets.Any(t => IsHorizontal(MapPlatform(t.Platform)));
            bool hasVertical = targets.Any(t => IsVertical(MapPlatform(t.Platform)));

            string mapHorizontal = "0:v"; // Video her zaman Input 0
            string mapVertical = "0:v";

            if (hasVertical)
            {
                sb.Append(" -filter_complex \"");

                if (hasHorizontal)
                {
                    // Hem Yatay Hem Dikey -> Split & Crop
                    sb.Append("[0:v]split=2[v_hor][v_raw_vert];");
                    sb.Append("[v_raw_vert]crop=w=ih*(9/16):h=ih:x=(iw-ow)/2:y=0[v_vert]");
                    mapHorizontal = "[v_hor]";
                    mapVertical = "[v_vert]";
                }
                else
                {
                    // Sadece Dikey -> Crop
                    sb.Append("[0:v]crop=w=ih*(9/16):h=ih:x=(iw-ow)/2:y=0[v_vert]");
                    mapVertical = "[v_vert]";
                }

                sb.Append("\" ");
            }

            // --- Çıkışlar (Outputs) ---
            foreach (var target in targets)
            {
                Platform p = MapPlatform(target.Platform);
                string videoSource = IsVertical(p) ? mapVertical : mapHorizontal;

                sb.Append($"-map {videoSource} ");
                sb.Append("-map 1:a "); // Ses her zaman Input 1

                sb.Append("-c:v libx264 -preset ultrafast -tune zerolatency ");
                sb.Append("-b:v 2500k -maxrate 2500k -bufsize 5000k ");
                sb.Append("-pix_fmt yuv420p ");
                sb.Append("-c:a aac -b:a 128k -ar 44100 ");
                sb.Append($"-f flv \"{target.Url}\" ");
            }

            return sb.ToString();
        }

        // --- Yardımcı Metotlar ---
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