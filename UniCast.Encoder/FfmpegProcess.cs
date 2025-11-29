using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UniCast.Encoder
{
    /// <summary>
    /// FFmpeg process yönetimi.
    /// DÜZELTME: Native interop hata yönetimi iyileştirildi.
    /// </summary>
    public sealed class FfmpegProcess : IDisposable
    {
        public event Action<string>? OnLog;
        public event Action<string>? OnMetric;
        public event Action<int?>? OnExit;

        private Process? _proc;
        private string _lastErrorLine = "";
        private bool _disposed;

        private const int GRACEFUL_TIMEOUT_MS = 3000;
        private const int KILL_TIMEOUT_MS = 2000;

        #region Native Interop

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, bool add);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GenerateConsoleCtrlEvent(int dwCtrlEvent, int dwProcessGroupId);

        [DllImport("kernel32.dll")]
        private static extern int GetLastError();

        private delegate bool ConsoleCtrlDelegate(int ctrlType);
        private const int CTRL_C_EVENT = 0;

        #endregion

        public static string ResolveFfmpegPath()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;

            var bundledPath = Path.Combine(baseDir, "External", "ffmpeg.exe");
            if (File.Exists(bundledPath)) return bundledPath;

            var localPath = Path.Combine(baseDir, "ffmpeg.exe");
            if (File.Exists(localPath)) return localPath;

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
                RedirectStandardInput = true
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
        /// DÜZELTME: Native interop hataları düzgün yönetiliyor.
        /// </summary>
        public async Task StopAsync()
        {
            if (_proc is null || _disposed) return;

            try
            {
                if (!_proc.HasExited)
                {
                    OnLog?.Invoke("[FFmpeg] Graceful shutdown başlatılıyor...");

                    // 1. AŞAMA: 'q' tuşu gönder
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

                    // 3. AŞAMA: Zorla kapat
                    OnLog?.Invoke("[FFmpeg] Graceful shutdown başarısız, Kill uygulanıyor...");

                    try
                    {
                        _proc.Kill(entireProcessTree: true);
                    }
                    catch (Win32Exception ex)
                    {
                        // DÜZELTME: Access denied veya process zaten kapanmış olabilir
                        OnLog?.Invoke($"[FFmpeg] Kill hatası (muhtemelen zaten kapandı): {ex.Message}");
                    }
                    catch (InvalidOperationException)
                    {
                        // Process zaten kapanmış
                    }

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

                try
                {
                    _proc?.Dispose();
                }
                catch { }

                _proc = null;
            }
        }

        private async Task<bool> TrySendQuitCommandAsync()
        {
            try
            {
                if (_proc == null || _proc.HasExited) return true;

                await _proc.StandardInput.WriteAsync('q');
                await _proc.StandardInput.FlushAsync();

                return await WaitForExitAsync(GRACEFUL_TIMEOUT_MS);
            }
            catch (IOException)
            {
                // Stdin kapalı olabilir
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// DÜZELTME: CTRL+C gönderme - hata yönetimi iyileştirildi.
        /// </summary>
        private async Task<bool> TrySendCtrlCAsync()
        {
            if (_proc == null || _proc.HasExited) return true;

            try
            {
                // Kendi console'umuzdan ayır
                if (!FreeConsole())
                {
                    // DÜZELTME: GetLastError ile hata nedenini al
                    var error = GetLastError();
                    System.Diagnostics.Debug.WriteLine($"[FFmpeg] FreeConsole failed: {error}");
                }

                // FFmpeg'in console'una bağlan
                if (!AttachConsole(_proc.Id))
                {
                    var error = GetLastError();
                    System.Diagnostics.Debug.WriteLine($"[FFmpeg] AttachConsole failed: {error}");
                    return false;
                }

                try
                {
                    // Kendi handler'ımızı devre dışı bırak
                    if (!SetConsoleCtrlHandler(null, true))
                    {
                        var error = GetLastError();
                        System.Diagnostics.Debug.WriteLine($"[FFmpeg] SetConsoleCtrlHandler(disable) failed: {error}");
                    }

                    // CTRL+C gönder
                    if (!GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0))
                    {
                        var error = GetLastError();
                        System.Diagnostics.Debug.WriteLine($"[FFmpeg] GenerateConsoleCtrlEvent failed: {error}");
                        return false;
                    }

                    // Handler'ı geri aç
                    SetConsoleCtrlHandler(null, false);
                }
                finally
                {
                    // Console'dan ayrıl
                    FreeConsole();
                }

                return await WaitForExitAsync(GRACEFUL_TIMEOUT_MS);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FFmpeg] CTRL+C exception: {ex.Message}");
                return false;
            }
        }

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
            catch (InvalidOperationException)
            {
                // Process zaten kapanmış
                return true;
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
                    try
                    {
                        _proc.Kill(entireProcessTree: true);
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                _proc?.Dispose();
            }
            catch { }

            _proc = null;

            // DÜZELTME: Event'leri temizle
            OnLog = null;
            OnMetric = null;
            OnExit = null;
        }
    }
}