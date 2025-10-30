using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UniCast.Encoder
{
    public sealed class FfmpegProcess : IDisposable
    {
        public event Action<string>? OnLog;
        public event Action<string>? OnMetric;
        public event Action<int?>? OnExit;
        private Process? _proc;

        public static string ResolveFfmpegPath()
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var local = Path.Combine(exeDir, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            var local2 = Path.Combine(exeDir, "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(local2)) return local2;
            return "ffmpeg";
        }

        public Task StartAsync(string args, CancellationToken ct)
        {
            if (_proc is not null) throw new InvalidOperationException("FFmpeg already running.");

            var psi = new ProcessStartInfo
            {
                FileName = ResolveFfmpegPath(),
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true
            };
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.Exited += (_, __) => OnExit?.Invoke(_proc?.ExitCode);

            if (!_proc.Start()) throw new InvalidOperationException("FFmpeg failed to start.");

            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = _proc.StandardError;
                    var buffer = new char[1024];
                    while (!_proc.HasExited && !ct.IsCancellationRequested)
                    {
                        var n = await reader.ReadAsync(buffer, 0, buffer.Length);
                        if (n > 0)
                        {
                            var s = new string(buffer, 0, n);
                            foreach (var line in s.Split('\n'))
                            {
                                var ln = line.TrimEnd();
                                if (IsMetric(ln)) OnMetric?.Invoke(ln);
                                else if (!string.IsNullOrWhiteSpace(ln)) OnLog?.Invoke(ln);
                            }
                        }
                        else
                        {
                            await Task.Delay(50, ct);
                        }
                    }
                }
                catch (Exception ex) { OnLog?.Invoke("stderr read error: " + ex.Message); }
            }, ct);

            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (_proc is null) return;
            try
            {
                if (!_proc.HasExited) { try { _proc.Kill(true); } catch { } }
            }
            finally
            {
                await Task.Delay(100);
                _proc?.Dispose();
                _proc = null;
            }
        }

        private static readonly Regex MetricRegex =
            new(@"frame=\s*\d+.*?fps=\s*([\d\.]+).*?bitrate=\s*([0-9\.kmbits\/]+).*?speed=\s*([0-9\.x]+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static bool IsMetric(string line)
            => !string.IsNullOrWhiteSpace(line) && (MetricRegex.IsMatch(line) || line.Contains("bitrate=") || line.Contains("fps="));

        public void Dispose() => _ = StopAsync();
    }
}
