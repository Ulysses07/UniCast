// UniCast.Encoder/FfmpegArgsBuilder.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UniCast.Core.Settings;
using CoreProfile = UniCast.Core.EncoderProfile;

namespace UniCast.Encoder
{
    public static class FfmpegArgsBuilder
    {
        /// <summary>
        /// Tek encode -> Çoklu RTMP çıkış (isteğe bağlı overlay) argümanlarını üretir.
        /// T türü TargetItem/StreamTarget vs. olabilir; URL alanlarını reflection ile bulur.
        /// </summary>
        public static string BuildSingleEncodeMultiRtmpWithOverlay<T>(
            SettingsData s,
            IReadOnlyList<T> targets,
            CoreProfile profile,
            string? overlayPipeName = null)
        {
            if (targets == null || targets.Count == 0)
                throw new ArgumentException("RTMP hedefi bulunamadı.");

            var fullUrls = targets
                .Select(t => GetFullOutputUrl(t))
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .ToList();

            if (fullUrls.Count == 0)
                throw new ArgumentException("Geçerli RTMP adresi üretilemedi.");

            var sb = new StringBuilder();

            // ------- INPUT (DirectShow) -------
            // Video
            if (!string.IsNullOrWhiteSpace(s.SelectedVideoDevice))
            {
                sb.Append($" -f dshow -rtbufsize 256M -video_size {s.Width}x{s.Height} -framerate {s.Fps} -i video=\"{s.SelectedVideoDevice}\"");
            }
            else
            {
                // Kamera yoksa testsrc
                sb.Append($" -f lavfi -i testsrc=size={s.Width}x{s.Height}:rate={s.Fps}");
            }

            // Audio
            if (!string.IsNullOrWhiteSpace(s.SelectedAudioDevice))
            {
                sb.Append($" -f dshow -i audio=\"{s.SelectedAudioDevice}\"");
            }
            else
            {
                // Ses yoksa anullsrc
                sb.Append(" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=48000");
            }

            // ------- OVERLAY (opsiyonel) -------
            bool useOverlay = !string.IsNullOrWhiteSpace(overlayPipeName);
            if (useOverlay)
            {
                // image2pipe üzerinden alfa kanallı PNG kare geliyorsa:
                sb.Append($" -thread_queue_size 512 -f image2pipe -r {s.Fps} -i \\\\.\\pipe\\{overlayPipeName}");
            }

            // ------- CODEC & Rate Control -------
            // Video codec
            var gop = Math.Max(1, s.Fps) * 2; // 2sn keyint
            if (s.UseNvenc)
            {
                sb.Append($" -c:v h264_nvenc -preset p4 -rc vbr -b:v {s.VideoBitrateKbps}k -maxrate {s.VideoBitrateKbps}k -bufsize {s.VideoBitrateKbps * 2}k -g {gop} -pix_fmt yuv420p");
            }
            else
            {
                sb.Append($" -c:v libx264 -preset veryfast -b:v {s.VideoBitrateKbps}k -maxrate {s.VideoBitrateKbps}k -bufsize {s.VideoBitrateKbps * 2}k -g {gop} -tune zerolatency -pix_fmt yuv420p -x264-params keyint={gop}:min-keyint={gop}:scenecut=0");
            }

            // Audio codec
            sb.Append($" -c:a aac -b:a {s.AudioBitrateKbps}k -ar 48000 -ac 2");

            // ------- FILTERGRAPH / MAP -------
            // Giriş indeksleri:
            // 0: video (testsrc/dshow), 1: audio (anullsrc/dshow), 2: overlay (varsa)
            if (useOverlay)
            {
                // 0:v üzerine 2:v bindir, audio 1:a
                sb.Append($" -filter_complex \"[0:v]fps={s.Fps},scale={s.Width}:{s.Height}[cam];[cam][2:v]overlay=0:0:format=auto[outv]\" -map \"[outv]\" -map 1:a");
            }
            else
            {
                // Doğrudan 0:v ve 1:a
                sb.Append(" -map 0:v -map 1:a");
            }

            // ------- Çoklu çıkış (tee) -------
            // onfail=ignore: tek hedef düşerse tüm süreç ölmesin.
            var teeSinks = string.Join("|", fullUrls.Select(u => $"[f=flv:onfail=ignore]{u}"));
            sb.Append($" -f tee \"{teeSinks}\"");

            // Streaming modunda latency için faydalı olabilir (opsiyonel):
            sb.Append(" -reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2");

            return sb.ToString().Trim();
        }

        // ================== Yardımcılar ==================

        private static string GetFullOutputUrl<T>(T target)
        {
            var o = (object)target;

            // Eğer sınıfta BuildFullUrl():string metodu varsa onu kullan.
            var m = o.GetType().GetMethod("BuildFullUrl", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (m != null && m.GetParameters().Length == 0 && m.ReturnType == typeof(string))
            {
                var r = m.Invoke(o, null) as string;
                if (!string.IsNullOrWhiteSpace(r)) return r!;
            }

            // Sık görülen property isimlerini dene
            var full = GetString(o, "FullUrl") ?? GetString(o, "Url") ?? GetString(o, "RtmpUrl");
            var server = GetString(o, "Server") ?? GetString(o, "ServerUrl") ?? GetString(o, "Host");
            var key = GetString(o, "Key") ?? GetString(o, "StreamKey") ?? GetString(o, "Path");

            if (!string.IsNullOrWhiteSpace(full) && full!.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(key) ? full! : Combine(full!, key!);
            }
            if (!string.IsNullOrWhiteSpace(server))
            {
                return string.IsNullOrWhiteSpace(key) ? server! : Combine(server!, key!);
            }

            // Son çare: ToString() RTMP dönerse
            var s = o.ToString();
            if (!string.IsNullOrWhiteSpace(s) && s!.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase))
                return s!;

            return string.Empty;

            static string Combine(string a, string b) => a.TrimEnd('/') + "/" + b.TrimStart('/');
        }

        private static string? GetString(object o, string name)
        {
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p != null && p.PropertyType == typeof(string))
                return p.GetValue(o) as string;
            return null;
        }
    }
}
