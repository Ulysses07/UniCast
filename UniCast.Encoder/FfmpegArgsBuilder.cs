using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UniCast.Core;
using UniCast.Core.Core;
using UniCast.Core.Settings;
using UniCast.Core.Streaming;
using UniCast.Encoder.Hardware;

namespace UniCast.Encoder
{
    public static class FfmpegArgsBuilder
    {
        private static string? _screenCaptureMethod;
        private static readonly object _probeLock = new();

        // DÜZELTME: Yapılandırılabilir buffer boyutları
        private const string DEFAULT_RTBUF_SIZE = "500M";
        private const int DEFAULT_THREAD_QUEUE_SIZE = 2048;

        /// <summary>
        /// v29: Hardware encoder kullanarak en iyi encoder parametrelerini al
        /// </summary>
        private static string GetOptimalEncoderParams(string? encoderName, int bitrate, int fps)
        {
            // Eğer manuel encoder belirtilmişse ve "auto" değilse, onu kullan
            if (!string.IsNullOrWhiteSpace(encoderName) && encoderName != "auto")
            {
                return encoderName switch
                {
                    var e when e.Contains("nvenc") => $"-c:v h264_nvenc -preset p1 -tune ll -rc cbr -b:v {bitrate}k",
                    var e when e.Contains("amf") => $"-c:v h264_amf -usage ultralowlatency -quality speed -rc cbr -b:v {bitrate}k",
                    var e when e.Contains("qsv") => $"-c:v h264_qsv -preset veryfast -b:v {bitrate}k",
                    _ => $"-c:v libx264 -preset ultrafast -tune zerolatency -b:v {bitrate}k"
                };
            }

            // v29: HardwareEncoderService ile otomatik en iyi encoder seçimi
            try
            {
                if (HardwareEncoderService.Instance.IsDetectionComplete &&
                    HardwareEncoderService.Instance.BestEncoder != null)
                {
                    var best = HardwareEncoderService.Instance.BestEncoder;
                    var parameters = HardwareEncoderService.Instance.GetEncoderParameters(
                        best.Type,
                        EncoderPreset.LowLatency,
                        bitrate,
                        fps);

                    System.Diagnostics.Debug.WriteLine($"[FfmpegArgsBuilder] Using hardware encoder: {best.Name}");
                    return parameters.ToFfmpegArgs();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FfmpegArgsBuilder] Hardware encoder error: {ex.Message}");
            }

            // Fallback: Software encoding
            return $"-c:v libx264 -preset ultrafast -tune zerolatency -b:v {bitrate}k";
        }

        public static string BuildFfmpegArgs(
            Profile profile,
            List<StreamTarget> targets,
            string? videoDeviceName,
            string? audioDeviceName,
            bool screenCapture,
            int audioDelayMs,
            string? localRecordPath,
            string? overlayPipeName,
            string? encoderName,
            string? rtbufSize = null,
            int? threadQueueSize = null)
        {
            var sb = new StringBuilder();

            // DÜZELTME: Yapılandırılabilir buffer
            var bufSize = rtbufSize ?? DEFAULT_RTBUF_SIZE;
            var queueSize = threadQueueSize ?? DEFAULT_THREAD_QUEUE_SIZE;

            // v29: Optimal encoder parametrelerini al (hardware destekli)
            string encParams = GetOptimalEncoderParams(encoderName, profile.VideoBitrateKbps, profile.Fps);

            // --- 1. GİRİŞLER ---
            sb.Append($"-f dshow -rtbufsize {bufSize} -thread_queue_size {queueSize} ");

            // INPUT 0: VİDEO
            if (screenCapture)
            {
                sb.Clear();
                sb.Append($"-rtbufsize {bufSize} -thread_queue_size {queueSize} ");

                var screenInput = GetScreenCaptureInput(profile.Fps);
                sb.Append(screenInput);
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

        private static string GetScreenCaptureInput(int fps)
        {
            if (_screenCaptureMethod == null)
            {
                lock (_probeLock)
                {
                    if (_screenCaptureMethod == null)
                    {
                        _screenCaptureMethod = DetectScreenCaptureMethod();
                    }
                }
            }

            return _screenCaptureMethod switch
            {
                "ddagrab" => $"-f ddagrab -framerate {fps} -i desktop ",
                "gdigrab" => $"-f gdigrab -framerate {fps} -i desktop ",
                _ => $"-f gdigrab -framerate {fps} -i desktop "
            };
        }

        private static string DetectScreenCaptureMethod()
        {
            try
            {
                var ffmpegPath = FfmpegProcess.ResolveFfmpegPath();
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -devices",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return "gdigrab";

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                var combined = (output + error).ToLowerInvariant();

                if (combined.Contains("ddagrab"))
                {
                    return "ddagrab";
                }

                return "gdigrab";
            }
            catch
            {
                return "gdigrab";
            }
        }

        public static void ResetScreenCaptureCache()
        {
            lock (_probeLock)
            {
                _screenCaptureMethod = null;
            }
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