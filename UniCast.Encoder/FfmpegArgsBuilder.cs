using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UniCast.Core.Models;
using UniCast.Core.Settings;
using UniCast.Encoder.Extensions; // ResolveUrl()

namespace UniCast.Encoder
{
    /// <summary>
    /// Tek encode → çoklu RTMP/RTMPS çıkış için FFmpeg argüman üretici.
    /// - Video kaynağı: ddagrab (ekran) | dshow (kamera) | lavfi testsrc (fallback)
    /// - Audio kaynağı: dshow (mikrofon) | lavfi anullsrc (fallback)
    /// - Çıkış: tee muxer ile birden fazla RTMP/RTMPS
    /// </summary>
    public static class FfmpegArgsBuilder
    {
        public static string BuildFfmpegArgs(
            Profile profile,
            IReadOnlyList<StreamTarget> targets,
            string? explicitVideoDevice = null,
            string? explicitAudioDevice = null,
            bool screenCapture = false
        )
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (targets == null || targets.Count == 0) throw new ArgumentException("En az bir hedef gerekli.", nameof(targets));

            // 1) Girişleri yapılandır
            var inputs = BuildInputs(profile, explicitVideoDevice, explicitAudioDevice, screenCapture);

            // 2) Encoder / mux parametreleri
            var enc = BuildEncoding(profile);
            var map = BuildMaps(inputs);

            // 3) Çıkışlar (tee)
            var tee = BuildTee(targets);

            // 4) Tümünü birleştir
            var args = string.Join(" ",
                inputs.VideoInput,
                inputs.AudioInput,
                enc,
                map,
                tee
            );

            return args;
        }

        // ---------------------- INPUTS ----------------------

        private sealed class Inputs
        {
            public string VideoInput { get; init; } = "";
            public string AudioInput { get; init; } = "";
            public bool HasVideo { get; init; }
            public bool HasAudio { get; init; }
            public int VideoIndex { get; init; } = -1; // -1 → lavfi testsrc / ddagrab / dshow video için 0
            public int AudioIndex { get; init; } = -1; // -1 → lavfi anullsrc / dshow audio için 1
        }

        private static Inputs BuildInputs(
            Profile p,
            string? explicitVideoDevice,
            string? explicitAudioDevice,
            bool screenCaptureFlag
        )
        {
            // Profile içinde DefaultCamera/Microphone yok → sadece explicit parametreleri kullan.
            var videoName = explicitVideoDevice?.Trim();
            var audioName = explicitAudioDevice?.Trim();

            // Ekran yakalama sinyali:
            // - açık işaret (StartAsync parametresi) veya
            // - videoName == "__SCREEN__"
            var wantScreen = screenCaptureFlag || string.Equals(videoName, "__SCREEN__", StringComparison.OrdinalIgnoreCase);

            // FRAMERATE / ÇÖZÜNÜRLÜK
            var fps = Math.Max(1, p.Fps);
            var width = Math.Max(16, p.Width);
            var height = Math.Max(16, p.Height);

            // --- Video input ---
            string videoInput;
            bool hasVideo = true;
            int vIndex = 0; // 0. input

            if (wantScreen)
            {
                // Windows 10+ → Desktop Duplication (ddagrab)
                videoInput = $"-f ddagrab -thread_queue_size 1024 -framerate {fps} -video_size {width}x{height} -draw_mouse 1 -i desktop";
            }
            else if (!string.IsNullOrWhiteSpace(videoName))
            {
                // Kamera
                // Cihaz adı FFmpeg’in -list_devices çıktısındaki “tam metin” olmalı.
                videoInput = $"-f dshow -thread_queue_size 1024 -rtbufsize 256M -i video=\"{videoName}\"";
            }
            else
            {
                // Fallback: testsrc
                hasVideo = false; // gerçek video yok, ama yine de video üreteceğiz (lavfi)
                vIndex = 0;
                videoInput = $"-f lavfi -i testsrc=size={width}x{height}:rate={fps}";
            }

            // --- Audio input ---
            string audioInput;
            bool hasAudio = true;
            int aIndex = 1; // ikinci input (0 video, 1 audio varsayımı)

            if (!string.IsNullOrWhiteSpace(audioName))
            {
                // Mikrofon
                audioInput = $"-f dshow -thread_queue_size 1024 -rtbufsize 256M -i audio=\"{audioName}\"";
            }
            else
            {
                // Fallback: sessiz
                hasAudio = false;
                audioInput = "-f lavfi -i anullsrc=cl=stereo:r=48000";
            }

            return new Inputs
            {
                VideoInput = videoInput,
                AudioInput = audioInput,
                HasVideo = hasVideo,
                HasAudio = hasAudio,
                VideoIndex = vIndex,
                AudioIndex = aIndex
            };
        }

        // ---------------------- ENCODING ----------------------

        private static string BuildEncoding(Profile p)
        {
            // Video codec seçimi: NVENC/QSV/AMF yoksa libx264
            var vcodec = string.IsNullOrWhiteSpace(p.VideoCodec) ? "libx264" : p.VideoCodec;
            var preset = string.IsNullOrWhiteSpace(p.VideoPreset) ? "veryfast" : p.VideoPreset;
            var vbit = Math.Max(100, p.VideoBitrateKbps);

            // Audio codec
            var acodec = string.IsNullOrWhiteSpace(p.AudioCodec) ? "aac" : p.AudioCodec;
            var abit = Math.Max(64, p.AudioBitrateKbps);

            var sb = new StringBuilder();
            sb.Append($"-c:v {vcodec} -preset {preset} -b:v {vbit}k -pix_fmt yuv420p ");
            sb.Append($"-c:a {acodec} -b:a {abit}k -ar 48000 -ac 2 ");
            // CBR / VBV güvenli ayarlar isterseniz buraya vbv_maxrate / bufsize ekleyebilirsiniz.

            return sb.ToString().Trim();
        }

        private static string BuildMaps(Inputs inputs)
        {
            // Her durumda 2 input başlatıyoruz (videoInput + audioInput).
            var vMap = "-map 0:v:0";
            var aMap = "-map 1:a:0";
            return $"{vMap} {aMap}";
        }

        // ---------------------- OUTPUTS (TEE) ----------------------

        private static string BuildTee(IReadOnlyList<StreamTarget> targets)
        {
            // tee: [onfail=ignore] ile bir hedef hata verirse diğerleri devam etsin.
            var chunks = targets
                .Where(t => t != null && !string.IsNullOrWhiteSpace(t.ResolveUrl()))
                .Select(t => $"[f=flv:onfail=ignore]{t.ResolveUrl()}")
                .ToArray();

            if (chunks.Length == 0)
                throw new InvalidOperationException("Geçerli RTMP/RTMPS hedefi yok.");

            var joined = string.Join("|", chunks);
            return $"-f tee \"{joined}\"";
        }
    }
}
