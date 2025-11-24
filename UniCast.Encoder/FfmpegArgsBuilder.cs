using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UniCast.Core.Core; // Platform enum'ı için
using UniCast.Core.Models; // CaptureDevice vb.
using UniCast.Core.Settings; // Profile
using UniCast.Core.Streaming; // StreamTarget

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

            // 1. Girişler (Inputs)
            // ---------------------------------------------------------
            // -rtbufsize: Canlı yayın için tampon bellek (donmaları azaltır)
            // -thread_queue_size: Paket sıkışmasını önler
            sb.Append("-f dshow -rtbufsize 100M -thread_queue_size 1024 ");

            // Video Girişi
            if (screenCapture)
            {
                // Ekran yakalama (GDI grab - basit yöntem)
                // Profesyonel kullanımda ddagrab (DX) tercih edilebilir ama şimdilik gdigrab yeterli
                // Not: dshow yerine gdigrab kullandığımız için yukarıdaki -f dshow'u ayırmak gerekebilir
                // Ancak basitlik adına burayı dshow ile kamera varsayıyoruz. 
                // Screen capture için ayrı bir mantık gerekir, şimdilik kamera odaklı gidiyoruz.
                // Eğer screen capture seçiliyse ve videoDeviceName boşsa hata vermemesi için:
                sb.Clear(); // dshow'u sil
                sb.Append("-f gdigrab -framerate 30 -i desktop ");
            }
            else
            {
                // Kamera Girişi
                if (!string.IsNullOrWhiteSpace(videoDeviceName))
                {
                    // video="Kamera Adı"
                    sb.Append($"-i video=\"{videoDeviceName}\"");

                    // Mikrofon Girişi (Aynı dshow içine ekleniyor)
                    if (!string.IsNullOrWhiteSpace(audioDeviceName))
                    {
                        sb.Append($":audio=\"{audioDeviceName}\" ");
                    }
                    else
                    {
                        sb.Append(" "); // Sadece video
                    }
                }
                else
                {
                    // Hiçbir cihaz yoksa test yayını (Color Bars)
                    sb.Clear();
                    sb.Append("-f lavfi -i testsrc=size=1280x720:rate=30 -f lavfi -i sine=frequency=1000 ");
                }
            }

            // 2. Filtreler (Akıllı Kırpma / Smart Crop)
            // ---------------------------------------------------------
            // Hangi platformlar var?
            bool hasHorizontal = targets.Any(t => IsHorizontal(t.Platform));
            bool hasVertical = targets.Any(t => IsVertical(t.Platform));

            string mapHorizontal = "0:v";
            string mapVertical = "0:v";

            // Eğer hem yatay hem dikey hedef varsa veya sadece dikey varsa filtre gerekir
            if (hasVertical)
            {
                // Filtre zinciri başlat
                sb.Append(" -filter_complex \"");

                if (hasHorizontal)
                {
                    // DURUM A: Hem Yatay Hem Dikey (YouTube + TikTok)
                    // Görüntüyü ikiye böl: [v_hor] ve [v_raw_vert]
                    sb.Append("[0:v]split=2[v_hor][v_raw_vert];");

                    // Dikey olanı kırp (Center Crop 9:16)
                    // w = h * (9/16)
                    // x = (in_w - out_w) / 2
                    sb.Append("[v_raw_vert]crop=w=ih*(9/16):h=ih:x=(iw-ow)/2:y=0[v_vert]");

                    mapHorizontal = "[v_hor]";
                    mapVertical = "[v_vert]";
                }
                else
                {
                    // DURUM B: Sadece Dikey (Sadece TikTok)
                    // Direkt kırp
                    sb.Append("[0:v]crop=w=ih*(9/16):h=ih:x=(iw-ow)/2:y=0[v_vert]");
                    mapVertical = "[v_vert]";
                }

                sb.Append("\" ");
            }

            // 3. Çıkışlar (Outputs)
            // ---------------------------------------------------------
            foreach (var target in targets)
            {
                // Platforma göre doğru görüntü kaynağını seç
                string videoSource = IsVertical(target.Platform) ? mapVertical : mapHorizontal;

                sb.Append($"-map {videoSource} "); // Video kaynağı
                sb.Append("-map 0:a? ");           // Ses kaynağı (varsa)

                // Codec Ayarları (H.264 - Çok hızlı)
                // preset ultrafast: CPU kullanımını minimumda tutar
                // tune zerolatency: Canlı yayın gecikmesini düşürür
                sb.Append("-c:v libx264 -preset ultrafast -tune zerolatency ");

                // Bitrate (Profil veya Ayarlardan gelmeli, şimdilik sabit)
                sb.Append("-b:v 2500k -maxrate 2500k -bufsize 5000k ");

                // Pixel Format (Uyumluluk için)
                sb.Append("-pix_fmt yuv420p ");

                // Ses Codec (AAC)
                sb.Append("-c:a aac -b:a 128k -ar 44100 ");

                // Çıkış Formatı (FLV - RTMP için standart)
                sb.Append($"-f flv \"{target.Url}\" ");
            }

            return sb.ToString();
        }

        // Hangi platformun dikey yayın istediğini belirler
        private static bool IsVertical(Platform p)
        {
            return p == Platform.TikTok || p == Platform.Instagram;
        }

        // Hangi platformun yatay yayın istediğini belirler
        private static bool IsHorizontal(Platform p)
        {
            return !IsVertical(p); // YouTube, Facebook, Twitch vb.
        }
    }
}