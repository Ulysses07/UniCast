using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UniCast.Core.Settings;
using UniCast.Core.Streaming;

namespace UniCast.Encoder
{
    /// <summary>
    /// Bazı çağrılar string döndürürken, bazıları Advisory+Args bekliyor.
    /// Bu küçük tip, geriye dönük uyumluluk için eklendi.
    /// </summary>
    public sealed class BuildResult
    {
        public string Args { get; init; } = "";
        public string? Advisory { get; init; } = null;
    }

    public static class FfmpegArgsBuilder
    {
        /// <summary>
        /// DirectShow (kamera+mikrofon) girişinden tek encode yapıp
        /// tee muxer ile birden fazla RTMP çıkışı üretir.
        /// includeOverlayPipe=true ise \\.\pipe\unicast_overlay girişini overlay olarak bind eder.
        /// </summary>
        public static string BuildSingleEncodeMultiRtmp(
            SettingsData s,
            IEnumerable<StreamTarget> targets,
            bool includeOverlayPipe)
        {
            // Geçerli URL'leri topla
            var targetUrls = (targets ?? Enumerable.Empty<StreamTarget>())
                .Select(t => t?.ResolveUrl() ?? "")
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .ToList();

            if (targetUrls.Count == 0)
                throw new InvalidOperationException("Geçerli RTMP hedefi yok.");

            var sb = new StringBuilder();

            // --- Video input ---
            if (!string.IsNullOrWhiteSpace(s.DefaultCamera))
            {
                // DirectShow kamera
                sb.Append($" -f dshow -thread_queue_size 512 -i video=\"{s.DefaultCamera}\"");
            }
            else
            {
                // Fallback test pattern
                sb.Append($" -f lavfi -i testsrc=size={s.Width}x{s.Height}:rate={s.Fps}");
            }

            // --- Audio input ---
            if (!string.IsNullOrWhiteSpace(s.DefaultMicrophone))
            {
                sb.Append($" -f dshow -thread_queue_size 512 -i audio=\"{s.DefaultMicrophone}\"");
            }
            else
            {
                // Sessiz kaynak
                sb.Append(" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100");
            }

            // --- Overlay pipe opsiyonel giriş ---
            if (includeOverlayPipe)
            {
                sb.Append(" -thread_queue_size 512 -f image2pipe -use_wallclock_as_timestamps 1 -i \\\\.\\pipe\\unicast_overlay");
            }

            // --- Video codec seçimi ---
            var enc = (s.Encoder ?? "x264").Trim().ToLowerInvariant();
            string vCodec = enc switch
            {
                "nvenc" => "h264_nvenc",
                "qsv" => "h264_qsv",
                "amf" => "h264_amf",
                "x264" => "libx264",
                "auto" => "libx264",
                _ => "libx264"
            };

            // --- Filtergraph ---
            // Giriş indeksleri:
            // 0: video (kamera veya testsrc)
            // 1: audio (mikrofon veya anullsrc)
            // 2: overlay (varsa)
            string vFilter;
            if (includeOverlayPipe)
            {
                // PNG alpha overlay'i 0:0 konuma bind et
                vFilter = $"[0:v]scale={s.Width}:{s.Height}[base];[base][2:v]overlay=0:0:format=auto";
            }
            else
            {
                vFilter = $"scale={s.Width}:{s.Height}";
            }

            sb.Append($" -filter_complex \"{vFilter}\"");

            // Video/audio map
            sb.Append(" -map 0:v -map 1:a");

            // Ortak encode ayarları
            var vb = Math.Max(300, s.VideoKbps); // güvenli alt sınır
            var ab = Math.Max(64, s.AudioKbps);

            sb.Append($" -c:v {vCodec} -b:v {vb}k -maxrate {vb}k -bufsize {Math.Max(1, vb / 2)}k");
            sb.Append($" -preset veryfast -g {Math.Max(2, s.Fps) * 2} -r {s.Fps}");
            sb.Append($" -c:a aac -b:a {ab}k -ar 44100 -ac 2");

            // Çoklu çıkış: tee muxer
            var teeParts = targetUrls.Select(u => $"[f=flv]{u}");
            sb.Append($" -f tee \"{string.Join("|", teeParts)}\"");

            return sb.ToString().Trim();
        }

        /// <summary>
        /// StreamController tarafının beklediği imza:
        /// overlayX/overlayY parametreleri şimdilik advisory amaçlı. Filtreye konum sabit 0:0 veriyoruz.
        /// Geriye Advisory+Args dönen bir sonuç verir (geriye dönük uyumluluk).
        /// </summary>
        public static BuildResult BuildSingleEncodeMultiRtmpWithOverlay(
            SettingsData s,
            IEnumerable<StreamTarget> targets,
            int overlayX = 0,
            int overlayY = 0,
            int overlayFps = 30)
        {
            // Not: overlayX/overlayY henüz doğrudan FFmpeg arg'ına işlenmiyor; ihtiyaca göre
            // overlay=overlayX:overlayY şeklinde değiştirilebilir. Şimdilik 0:0 sabit.
            var args = BuildSingleEncodeMultiRtmp(s, targets, includeOverlayPipe: true);

            // Kullanıcıya olası notları döndür (örnek)
            string? advisory = null;
            if (string.IsNullOrWhiteSpace(s.DefaultCamera))
                advisory = "Uyarı: Kamera seçili değil, 'testsrc' kullanılacak.";
            else if (string.IsNullOrWhiteSpace(s.DefaultMicrophone))
                advisory = "Uyarı: Mikrofon seçili değil, 'anullsrc' kullanılacak.";

            return new BuildResult
            {
                Args = args,
                Advisory = advisory
            };
        }
    }
}
