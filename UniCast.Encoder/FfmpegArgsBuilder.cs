using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UniCast.Core.Core; // Platform Enum'ı burada
using UniCast.Core.Settings;
using UniCast.Core.Streaming; // StreamPlatform ve StreamTarget burada

namespace UniCast.Encoder
{
    public static class FfmpegArgsBuilder
    {
        public static string BuildFfmpegArgs(
            Profile profile,
            List<StreamTarget> targets,
            string? videoDeviceName,
            string? audioDeviceName,
            bool screenCapture)
        {
            var sb = new StringBuilder();

            // --- Girişler (Inputs) ---
            sb.Append("-f dshow -rtbufsize 100M -thread_queue_size 1024 ");

            if (screenCapture)
            {
                sb.Clear();
                sb.Append("-f gdigrab -framerate 30 -i desktop ");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(videoDeviceName))
                {
                    sb.Append($"-i video=\"{videoDeviceName}\"");
                    if (!string.IsNullOrWhiteSpace(audioDeviceName))
                        sb.Append($":audio=\"{audioDeviceName}\" ");
                    else
                        sb.Append(" ");
                }
                else
                {
                    sb.Clear();
                    sb.Append("-f lavfi -i testsrc=size=1280x720:rate=30 -f lavfi -i sine=frequency=1000 ");
                }
            }

            // --- Filtre Mantığı (Düzeltildi) ---

            // HATA DÜZELTME: StreamPlatform -> Platform dönüşümü yapıyoruz
            bool hasHorizontal = targets.Any(t => IsHorizontal(MapPlatform(t.Platform)));
            bool hasVertical = targets.Any(t => IsVertical(MapPlatform(t.Platform)));

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

            // --- Çıkışlar (Outputs) ---
            foreach (var target in targets)
            {
                // HATA DÜZELTME: Burada da MapPlatform kullanıyoruz
                Platform p = MapPlatform(target.Platform);
                string videoSource = IsVertical(p) ? mapVertical : mapHorizontal;

                sb.Append($"-map {videoSource} ");
                sb.Append("-map 0:a? ");

                sb.Append("-c:v libx264 -preset ultrafast -tune zerolatency ");
                sb.Append("-b:v 2500k -maxrate 2500k -bufsize 5000k ");
                sb.Append("-pix_fmt yuv420p ");
                sb.Append("-c:a aac -b:a 128k -ar 44100 ");
                sb.Append($"-f flv \"{target.Url}\" ");
            }

            return sb.ToString();
        }

        // --- Yardımcı Metotlar ---

        // YENİ EKLENEN: İki enum arası çeviri
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

        private static bool IsVertical(Platform p)
        {
            return p == Platform.TikTok || p == Platform.Instagram;
        }

        private static bool IsHorizontal(Platform p)
        {
            return !IsVertical(p);
        }
    }
}