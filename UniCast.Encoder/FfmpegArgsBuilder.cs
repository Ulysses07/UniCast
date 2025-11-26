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
            string? overlayPipeName) // YENİ PARAMETRE: Overlay Borusu
        {
            var sb = new StringBuilder();

            // --- 1. GİRİŞLER (Inputs) ---
            sb.Append("-f dshow -rtbufsize 100M -thread_queue_size 1024 ");

            // INPUT 0: VİDEO (Kamera/Ekran)
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

            // INPUT 1: SES (Mikrofon)
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

            // INPUT 2: OVERLAY (Varsa)
            int overlayIndex = -1;
            if (!string.IsNullOrEmpty(overlayPipeName))
            {
                // Named Pipe üzerinden gelen PNG akışını okuyoruz
                sb.Append($"-f image2pipe -framerate 30 -i \"\\\\.\\pipe\\{overlayPipeName}\" ");
                overlayIndex = 2; // Video(0), Ses(1), Overlay(2)
            }

            // --- 2. FİLTRELER (Smart Crop + Overlay) ---
            bool hasHorizontal = targets.Any(t => IsHorizontal(MapPlatform(t.Platform)));
            bool hasVertical = targets.Any(t => IsVertical(MapPlatform(t.Platform)));
            if (!string.IsNullOrEmpty(localRecordPath)) hasHorizontal = true; // Kayıt genelde yataydır

            // Karmaşık filtre zinciri gerekiyorsa başlat
            bool needsFilter = (overlayIndex != -1) || hasVertical;

            string vMain = "0:v"; // İşlenecek ana video kaynağı
            string mapHorizontal = "0:v";
            string mapVertical = "0:v";

            if (needsFilter)
            {
                sb.Append(" -filter_complex \"");

                // A. Önce Overlay'i Yapıştır (Varsa)
                if (overlayIndex != -1)
                {
                    // [0:v][2:v]overlay=0:0[v_overlaid]
                    // EOF_ACTION=pass: Overlay kesilirse yayın durmasın
                    sb.Append($"[{vMain}][{overlayIndex}:v]overlay=0:0:eof_action=pass[v_overlaid];");
                    vMain = "[v_overlaid]";
                }

                // B. Sonra Kırpma/Bölme İşlemleri (Smart Crop)
                if (hasVertical)
                {
                    if (hasHorizontal)
                    {
                        // Hem Yatay Hem Dikey -> İkiye böl
                        sb.Append($"{vMain}split=2[v_hor][v_raw_vert];");
                        // Dikey olanı ortadan kırp (9:16)
                        sb.Append("[v_raw_vert]crop=w=ih*(9/16):h=ih:x=(iw-ow)/2:y=0[v_vert]");

                        mapHorizontal = "[v_hor]";
                        mapVertical = "[v_vert]";
                    }
                    else
                    {
                        // Sadece Dikey -> Direkt kırp
                        sb.Append($"{vMain}crop=w=ih*(9/16):h=ih:x=(iw-ow)/2:y=0[v_vert]");
                        mapVertical = "[v_vert]";
                    }
                }
                else
                {
                    // Sadece Yatay (ama Overlay eklenmiş hali)
                    mapHorizontal = vMain;
                }

                // Eğer son karakter noktalı virgülse temizle (FFmpeg hata vermesin diye)
                if (sb[sb.Length - 1] == ';') sb.Remove(sb.Length - 1, 1);

                sb.Append("\" ");
            }
            else
            {
                // Hiç filtre yoksa, varsayılan kaynaklar kullanılır (0:v)
            }

            // --- 3. ÇIKIŞLAR (Outputs) ---
            foreach (var target in targets)
            {
                Platform p = MapPlatform(target.Platform);
                string videoSource = IsVertical(p) ? mapVertical : mapHorizontal;

                sb.Append($"-map {videoSource} -map 1:a "); // Ses her zaman Input 1
                sb.Append("-c:v libx264 -preset ultrafast -tune zerolatency ");
                sb.Append("-b:v 2500k -maxrate 2500k -bufsize 5000k ");
                sb.Append("-pix_fmt yuv420p ");
                sb.Append("-c:a aac -b:a 128k -ar 44100 ");
                sb.Append($"-f flv \"{target.Url}\" ");
            }

            // --- YEREL KAYIT ---
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