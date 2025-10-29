using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Core;

namespace UniCast.Encoder
{
    /// <summary>
    /// Tek encode üretip, FFmpeg 'tee' muxer ile aynı anda N hedefe (rtmp/rtmps) yayınlar.
    /// Kaynak: dshow (kamera/mikrofon) verilirse gerçek cihazdan; yoksa lavfi test kaynakları.
    /// </summary>
    public sealed class FfmpegProcess : IEncoderService, IDisposable
    {
        private Process? _ffmpeg;
        private readonly object _gate = new();
        private bool _isRunning;
        private Task? _readTask;
        private CancellationTokenSource? _pumpCts;

        public event Action<EncoderMetrics>? OnMetrics;

        public bool IsRunning
        {
            get { lock (_gate) return _isRunning; }
            private set { lock (_gate) _isRunning = value; }
        }

        private static string ResolveFfmpegPath()
        {
            var baseDir = AppContext.BaseDirectory;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var local = Path.Combine(baseDir, "ffmpeg.exe");
                if (File.Exists(local)) return local;
            }
            return "ffmpeg";
        }

        // IEncoderService gereği: kamera/mikrofon opsiyonları ile StartAsync
        public async Task StartAsync(
            EncoderProfile profile,
            IReadOnlyList<string> rtmpTargets,
            string? cameraDevice,
            string? micDevice,
            CancellationToken ct)
        {
            if (rtmpTargets == null || rtmpTargets.Count == 0)
                throw new InvalidOperationException("En az bir RTMP/RTMPS hedefi gerekli.");

            if (IsRunning)
                throw new InvalidOperationException("Encoder zaten çalışıyor.");

            // URL’leri normalize et
            var validTargets = new List<string>(rtmpTargets.Count);
            foreach (var raw in rtmpTargets)
            {
                var u = (raw ?? "").Trim();
                if (string.IsNullOrWhiteSpace(u)) continue;
                if (!u.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) &&
                    !u.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
                    u = "rtmp://" + u.TrimStart('/');
                validTargets.Add(u);
            }
            if (validTargets.Count == 0)
                throw new InvalidOperationException("Geçerli hedef URL bulunamadı.");

            var ffmpegPath = ResolveFfmpegPath();
            var vEncoder = ChooseVideoEncoder(ffmpegPath);

            var args = BuildArgs(profile, validTargets, vEncoder, cameraDevice, micDevice);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8
            };

            _ffmpeg = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _ffmpeg.Exited += (_, __) => IsRunning = false;

            _pumpCts = new CancellationTokenSource();
            try
            {
                if (!_ffmpeg.Start())
                    throw new InvalidOperationException("FFmpeg başlatılamadı.");

                IsRunning = true;
                _readTask = Task.Run(() => PumpStderrForMetrics(_ffmpeg, _pumpCts.Token), _pumpCts.Token);
            }
            catch
            {
                SafeKill();
                throw;
            }

            await Task.CompletedTask;
        }

        // IEncoderService'in eksik StartAsync metodu implementasyonu eklendi
        public async Task StartAsync(
            EncoderProfile profile,
            IReadOnlyList<string> rtmpTargets,
            CancellationToken ct)
        {
            await StartAsync(profile, rtmpTargets, null, null, ct);
        }

        public async Task StopAsync(CancellationToken ct)
        {
            if (!IsRunning) return;

            try
            {
                SafeKill();
                if (_pumpCts != null && !_pumpCts.IsCancellationRequested)
                    _pumpCts.Cancel();

                if (_readTask != null)
                {
                    try { await _readTask.ConfigureAwait(false); } catch { /* ignore */ }
                }
            }
            finally
            {
                IsRunning = false;
            }
        }

        private void SafeKill()
        {
            try
            {
                if (_ffmpeg != null && !_ffmpeg.HasExited)
                {
                    _ffmpeg.Kill(entireProcessTree: true);
                    _ffmpeg.WaitForExit(3000);
                }
            }
            catch { }
            finally
            {
                try { _ffmpeg?.Dispose(); } catch { }
                _ffmpeg = null;
            }
        }

        private static string ChooseVideoEncoder(string ffmpegPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -encoders",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                using var p = Process.Start(psi);
                if (p == null) return "libx264";

                var text = p.StandardOutput.ReadToEnd() + "\n" + p.StandardError.ReadToEnd();
                p.WaitForExit(2000);

                if (text.Contains("h264_nvenc", StringComparison.OrdinalIgnoreCase)) return "h264_nvenc";
                if (text.Contains("h264_amf", StringComparison.OrdinalIgnoreCase)) return "h264_amf";
                if (text.Contains("h264_qsv", StringComparison.OrdinalIgnoreCase)) return "h264_qsv";
            }
            catch { }
            return "libx264";
        }

        private static string BuildArgs(
            EncoderProfile p, List<string> targets, string videoEncoder,
            string? cameraDevice, string? micDevice)
        {
            int gop = Math.Max(2, p.Fps * 2);
            int maxrate = p.VideoKbps;
            int bufsize = p.VideoKbps * 2;

            var tee = BuildTeeOutput(targets);

            var sb = new StringBuilder();
            sb.Append("-fflags +genpts ");

            // ------------ INPUTLAR ------------
            bool useDshow = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                            (!string.IsNullOrWhiteSpace(cameraDevice) || !string.IsNullOrWhiteSpace(micDevice));

            if (useDshow)
            {
                if (!string.IsNullOrWhiteSpace(micDevice))
                    sb.AppendFormat(CultureInfo.InvariantCulture, "-f dshow -i audio=\"{0}\" ", micDevice!.Replace("\"", "\\\""));
                else
                    sb.Append("-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 ");

                if (!string.IsNullOrWhiteSpace(cameraDevice))
                    sb.AppendFormat(CultureInfo.InvariantCulture, "-f dshow -i video=\"{0}\" ", cameraDevice!.Replace("\"", "\\\""));
                else
                    sb.AppendFormat(CultureInfo.InvariantCulture, "-f lavfi -i testsrc=size={0}x{1}:rate={2} ", p.Width, p.Height, p.Fps);

                sb.Append("-map 1:v -map 0:a -pix_fmt yuv420p ");
            }
            else
            {
                sb.Append("-re -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 ");
                sb.AppendFormat(CultureInfo.InvariantCulture, "-f lavfi -i testsrc=size={0}x{1}:rate={2} ", p.Width, p.Height, p.Fps);
                sb.Append("-map 1:v -map 0:a -pix_fmt yuv420p ");
            }

            // ------------ ENCODE ------------
            if (videoEncoder == "h264_nvenc")
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "-c:v h264_nvenc -preset p5 -tune hq -rc cbr -b:v {0}k -maxrate {1}k -bufsize {2}k -g {3} ",
                    p.VideoKbps, maxrate, bufsize, gop);
            }
            else if (videoEncoder == "h264_amf")
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "-c:v h264_amf -quality quality -usage transcoding -b:v {0}k -maxrate {1}k -bufsize {2}k -g {3} ",
                    p.VideoKbps, maxrate, bufsize, gop);
            }
            else if (videoEncoder == "h264_qsv")
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "-c:v h264_qsv -global_quality 20 -look_ahead 0 -b:v {0}k -maxrate {1}k -bufsize {2}k -g {3} ",
                    p.VideoKbps, maxrate, bufsize, gop);
            }
            else
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "-c:v libx264 -preset veryfast -tune zerolatency -b:v {0}k -maxrate {1}k -bufsize {2}k -g {3} ",
                    p.VideoKbps, maxrate, bufsize, gop);
            }

            sb.AppendFormat(CultureInfo.InvariantCulture, "-c:a aac -b:a {0}k -ar 44100 -ac 2 ", p.AudioKbps);

            // ------------ OUTPUT ------------
            sb.Append("-flvflags no_duration_filesize -f tee ");
            sb.Append(Quote(tee));

            return sb.ToString();
        }

        private static string BuildTeeOutput(List<string> targets)
        {
            var parts = new List<string>(targets.Count);
            foreach (var url in targets)
            {
                var entry = $"[f=flv:onfail=ignore]{url.Trim()}";
                parts.Add(entry);
            }
            return string.Join("|", parts);
        }

        private static string Quote(string s)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var escaped = s.Replace("\"", "\\\"");
                return $"\"{escaped}\"";
            }
            return $"'{s.Replace("'", "'\\''")}'";
        }

        private async Task PumpStderrForMetrics(Process ffmpeg, CancellationToken token)
        {
            try
            {
                using var reader = ffmpeg.StandardError;
                string? line;

                var reFps = new Regex(@"fps=\s*(\d+(\.\d+)?)", RegexOptions.Compiled);
                var reBit = new Regex(@"bitrate=\s*(\d+(\.\d+)?)kbits/s", RegexOptions.Compiled);

                double fps = 0;
                double bKbps = 0;

                while (!token.IsCancellationRequested && (line = await reader.ReadLineAsync()) != null)
                {
                    var m1 = reFps.Match(line);
                    if (m1.Success && double.TryParse(m1.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var f))
                        fps = f;

                    var m2 = reBit.Match(line);
                    if (m2.Success && double.TryParse(m2.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var kb))
                        bKbps = kb;

                    OnMetrics?.Invoke(new EncoderMetrics(
                        fps: fps,
                        dropPercent: 0,
                        bitrateKbps: (int)Math.Round(bKbps),
                        at: DateTimeOffset.Now,
                        videoKbps: bKbps,
                        audioKbps: 0
                    ));
                }
            }
            catch
            {
                // proses kapanmış olabilir, önemsemeyelim
            }
        }

        // Basit dshow tarayıcı (Windows)
        public static (string[] video, string[] audio) ListDshowDevices()
        {
            var ffmpeg = ResolveFfmpegPath();
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = "-hide_banner -f dshow -list_devices true -i dummy",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var p = Process.Start(psi)!;
            var txt = (p.StandardOutput.ReadToEnd() + "\n" + p.StandardError.ReadToEnd());
            p.WaitForExit(2000);

            var video = new List<string>();
            var audio = new List<string>();
            using var sr = new StringReader(txt);
            string? line;
            bool inVideo = false, inAudio = false;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase)) { inVideo = true; inAudio = false; continue; }
                if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase)) { inVideo = false; inAudio = true; continue; }
                var m = Regex.Match(line, @"\s*""(.+?)""");
                if (m.Success)
                {
                    if (inVideo) video.Add(m.Groups[1].Value);
                    else if (inAudio) audio.Add(m.Groups[1].Value);
                }
            }
            return (video.ToArray(), audio.ToArray());
        }

        public void Dispose()
        {
            try { _pumpCts?.Cancel(); } catch { }
            SafeKill();
            _pumpCts?.Dispose();
            _pumpCts = null;
        }
    }
}
