using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
        private string _lastErrorLine = "";
        private bool _disposed;

        // Graceful shutdown için
        private const int GRACEFUL_TIMEOUT_MS = 3000;
        private const int KILL_TIMEOUT_MS = 2000;

        // Native console control için
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, bool add);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GenerateConsoleCtrlEvent(int dwCtrlEvent, int dwProcessGroupId);

        private delegate bool ConsoleCtrlDelegate(int ctrlType);
        private const int CTRL_C_EVENT = 0;

        public static string ResolveFfmpegPath()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 1. External klasörü
            var bundledPath = Path.Combine(baseDir, "External", "ffmpeg.exe");
            if (File.Exists(bundledPath)) return bundledPath;

            // 2. Ana dizin
            var localPath = Path.Combine(baseDir, "ffmpeg.exe");
            if (File.Exists(localPath)) return localPath;

            // 3. PATH
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var path in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    var fullPath = Path.Combine(path.Trim(), "ffmpeg.exe");
                    if (File.Exists(fullPath)) return fullPath;
                }
                catch { }
            }

            return "ffmpeg";
        }

        public Task StartAsync(string args, CancellationToken ct)
        {
            if (_proc is not null)
                throw new InvalidOperationException("FFmpeg zaten çalışıyor.");

            var ffmpegPath = ResolveFfmpegPath();

            if (ffmpegPath != "ffmpeg" && !File.Exists(ffmpegPath))
                throw new FileNotFoundException("FFmpeg dosyası bulunamadı!", ffmpegPath);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true // DÜZELTME: 'q' göndermek için
            };

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            _proc.Exited += (_, __) =>
            {
                if (!string.IsNullOrWhiteSpace(_lastErrorLine))
                {
                    var meaning = ErrorDictionary.Translate(_lastErrorLine);
                    OnLog?.Invoke("FFmpeg Hata Analizi: " + meaning);
                }

                OnExit?.Invoke(_proc?.ExitCode);
            };

            if (!_proc.Start())
                throw new InvalidOperationException("FFmpeg başlatılamadı.");

            // Log okuma task'ı
            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = _proc.StandardError;
                    var buffer = new char[2048];

                    while (!_proc.HasExited && !ct.IsCancellationRequested)
                    {
                        var n = await reader.ReadAsync(buffer, 0, buffer.Length);
                        if (n > 0)
                        {
                            var chunk = new string(buffer, 0, n);

                            foreach (var line in chunk.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                var ln = line.Trim();
                                if (string.IsNullOrWhiteSpace(ln)) continue;

                                _lastErrorLine = ln;

                                if (IsMetric(ln))
                                    OnMetric?.Invoke(ln);
                                else
                                    OnLog?.Invoke(ln);
                            }
                        }
                        else
                        {
                            await Task.Delay(50, ct);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    OnLog?.Invoke("FFmpeg Okuma Hatası: " + ex.Message);
                }
            }, ct);

            return Task.CompletedTask;
        }

        /// <summary>
        /// FFmpeg'i düzgünce kapatır.
        /// DÜZELTME: Önce 'q' tuşu, sonra CTRL+C, en son Kill
        /// </summary>
        public async Task StopAsync()
        {
            if (_proc is null || _disposed) return;

            try
            {
                if (!_proc.HasExited)
                {
                    OnLog?.Invoke("[FFmpeg] Graceful shutdown başlatılıyor...");

                    // 1. AŞAMA: 'q' tuşu gönder (FFmpeg'in standart kapatma komutu)
                    if (await TrySendQuitCommandAsync())
                    {
                        OnLog?.Invoke("[FFmpeg] 'q' komutu ile kapatıldı.");
                        return;
                    }

                    // 2. AŞAMA: CTRL+C sinyali gönder
                    if (await TrySendCtrlCAsync())
                    {
                        OnLog?.Invoke("[FFmpeg] CTRL+C ile kapatıldı.");
                        return;
                    }

                    // 3. AŞAMA: Zorla kapat (son çare)
                    OnLog?.Invoke("[FFmpeg] Graceful shutdown başarısız, Kill uygulanıyor...");
                    _proc.Kill(entireProcessTree: true);
                    await WaitForExitAsync(KILL_TIMEOUT_MS);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[FFmpeg] Stop hatası: {ex.Message}");
            }
            finally
            {
                await Task.Delay(100);
                _proc?.Dispose();
                _proc = null;
            }
        }

        /// <summary>
        /// FFmpeg stdin'e 'q' karakteri gönderir.
        /// </summary>
        private async Task<bool> TrySendQuitCommandAsync()
        {
            try
            {
                if (_proc == null || _proc.HasExited) return true;

                // 'q' tuşunu stdin'e gönder
                await _proc.StandardInput.WriteAsync('q');
                await _proc.StandardInput.FlushAsync();

                // Kapamasını bekle
                return await WaitForExitAsync(GRACEFUL_TIMEOUT_MS);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Process'e CTRL+C sinyali gönderir.
        /// </summary>
        private async Task<bool> TrySendCtrlCAsync()
        {
            try
            {
                if (_proc == null || _proc.HasExited) return true;

                // Windows'ta CTRL+C göndermek karmaşık
                // Kendi console'umuzdan ayır
                FreeConsole();

                // FFmpeg'in console'una bağlan
                if (AttachConsole(_proc.Id))
                {
                    // Kendi handler'ımızı devre dışı bırak
                    SetConsoleCtrlHandler(null, true);

                    // CTRL+C gönder
                    GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);

                    // Handler'ı geri aç
                    SetConsoleCtrlHandler(null, false);

                    // Console'dan ayrıl
                    FreeConsole();

                    return await WaitForExitAsync(GRACEFUL_TIMEOUT_MS);
                }
            }
            catch
            {
                // CTRL+C başarısız olursa devam et
            }

            return false;
        }

        /// <summary>
        /// Process'in kapanmasını bekler.
        /// </summary>
        private async Task<bool> WaitForExitAsync(int timeoutMs)
        {
            if (_proc == null) return true;

            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                await _proc.WaitForExitAsync(cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private static readonly Regex MetricRegex =
            new(@"frame=\s*\d+.*?fps=\s*([\d\.]+).*?bitrate=\s*([0-9\.kmbits\/]+).*?speed=\s*([0-9\.x]+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static bool IsMetric(string line)
            => !string.IsNullOrWhiteSpace(line) &&
               (MetricRegex.IsMatch(line) ||
                line.Contains("bitrate=") ||
                line.Contains("fps="));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_proc != null && !_proc.HasExited)
                {
                    _proc.Kill(entireProcessTree: true);
                }
            }
            catch { }

            _proc?.Dispose();
            _proc = null;
        }
    }
}