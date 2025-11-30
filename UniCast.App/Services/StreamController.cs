using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.Core.Services
{
    /// <summary>
    /// Stream işlemlerini yöneten merkezi kontrolcü.
    /// FFmpeg process'lerini yönetir.
    /// Thread-safe implementasyon.
    /// </summary>
    public sealed class StreamController : IDisposable
    {
        private static readonly Lazy<StreamController> _instance = new(
            () => new StreamController(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static StreamController Instance => _instance.Value;

        // Thread-safe state
        private volatile bool _isRunning;
        private readonly object _stateLock = new();

        private Process? _ffmpegProcess;
        private CancellationTokenSource? _cts;
        private Task? _monitorTask;
        private bool _disposed;

        // Aktif streamler
        private readonly ConcurrentDictionary<string, StreamInfo> _activeStreams = new();

        // Events
        public event Action<string, string>? LogMessage; // (level, message)
        public event EventHandler<StreamStateChangedEventArgs>? StateChanged;
        public event EventHandler<StreamStatisticsEventArgs>? StatisticsUpdated;

        // Properties
        public bool IsRunning
        {
            get
            {
                lock (_stateLock)
                {
                    return _isRunning;
                }
            }
            private set
            {
                lock (_stateLock)
                {
                    if (_isRunning != value)
                    {
                        _isRunning = value;
                        OnStateChanged(value ? StreamState.Running : StreamState.Stopped);
                    }
                }
            }
        }

        public string? FfmpegPath { get; set; }
        public TimeSpan ProcessTimeout { get; set; } = TimeSpan.FromSeconds(30);

        private StreamController()
        {
            // FFmpeg path'i belirle
            FfmpegPath = FindFfmpegPath();
        }

        /// <summary>
        /// Stream'i başlatır.
        /// </summary>
        public async Task<bool> StartAsync(StreamConfiguration config, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            if (IsRunning)
            {
                RaiseLog("warning", "Stream zaten çalışıyor");
                return false;
            }

            if (string.IsNullOrEmpty(FfmpegPath) || !File.Exists(FfmpegPath))
            {
                RaiseLog("error", "FFmpeg bulunamadı");
                return false;
            }

            lock (_stateLock)
            {
                if (_isRunning)
                    return false;

                _cts?.Dispose();
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            }

            try
            {
                RaiseLog("info", $"Stream başlatılıyor: {config.OutputUrl}");

                var args = BuildFfmpegArgs(config);
                RaiseLog("debug", $"FFmpeg args: {args}");

                var startInfo = new ProcessStartInfo
                {
                    FileName = FfmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true
                };

                _ffmpegProcess = new Process { StartInfo = startInfo };
                _ffmpegProcess.OutputDataReceived += OnFfmpegOutput;
                _ffmpegProcess.ErrorDataReceived += OnFfmpegError;

                if (!_ffmpegProcess.Start())
                {
                    RaiseLog("error", "FFmpeg başlatılamadı");
                    return false;
                }

                _ffmpegProcess.BeginOutputReadLine();
                _ffmpegProcess.BeginErrorReadLine();

                IsRunning = true;

                // Stream bilgisini kaydet
                _activeStreams[config.StreamId] = new StreamInfo
                {
                    Id = config.StreamId,
                    OutputUrl = config.OutputUrl,
                    StartedAt = DateTime.UtcNow,
                    ProcessId = _ffmpegProcess.Id
                };

                // Monitor task başlat
                _monitorTask = MonitorProcessAsync(_cts.Token);

                RaiseLog("info", "Stream başarıyla başlatıldı");
                return true;
            }
            catch (Exception ex)
            {
                RaiseLog("error", $"Stream başlatma hatası: {ex.Message}");
                Log.Error(ex, "[StreamController] Start hatası");

                CleanupProcess();
                return false;
            }
        }

        /// <summary>
        /// Stream'i durdurur.
        /// </summary>
        public async Task StopAsync()
        {
            if (!IsRunning)
                return;

            RaiseLog("info", "Stream durduruluyor...");

            try
            {
                _cts?.Cancel();

                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    // Önce nazikçe kapat (q tuşu ile)
                    try
                    {
                        _ffmpegProcess.StandardInput.WriteLine("q");
                        _ffmpegProcess.StandardInput.Flush();

                        if (!_ffmpegProcess.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
                        {
                            RaiseLog("warning", "FFmpeg yanıt vermedi, zorla kapatılıyor");
                            _ffmpegProcess.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                        try { _ffmpegProcess.Kill(); } catch { }
                    }
                }

                // Monitor task'ın bitmesini bekle
                if (_monitorTask != null)
                {
                    try
                    {
                        await _monitorTask.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (TimeoutException)
                    {
                        RaiseLog("warning", "Monitor task timeout");
                    }
                    catch (OperationCanceledException)
                    {
                        // Beklenen
                    }
                }
            }
            catch (Exception ex)
            {
                RaiseLog("error", $"Durdurma hatası: {ex.Message}");
                Log.Error(ex, "[StreamController] Stop hatası");
            }
            finally
            {
                CleanupProcess();
                IsRunning = false;
                _activeStreams.Clear();
                RaiseLog("info", "Stream durduruldu");
            }
        }

        /// <summary>
        /// Stream'i yeniden başlatır.
        /// </summary>
        public async Task<bool> RestartAsync(StreamConfiguration config, CancellationToken ct = default)
        {
            await StopAsync();
            await Task.Delay(1000, ct); // Kısa bekle
            return await StartAsync(config, ct);
        }

        /// <summary>
        /// Stream'i durdurur (sync versiyon).
        /// </summary>
        public void Stop()
        {
            StopAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Aktif stream bilgilerini döndürür.
        /// </summary>
        public StreamInfo[] GetActiveStreams()
        {
            return _activeStreams.Values.ToArray();
        }

        #region Private Methods

        private async Task MonitorProcessAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    await Task.Delay(1000, ct);
                }

                if (_ffmpegProcess != null && _ffmpegProcess.HasExited)
                {
                    var exitCode = _ffmpegProcess.ExitCode;
                    RaiseLog(exitCode == 0 ? "info" : "warning", $"FFmpeg sonlandı. Exit code: {exitCode}");

                    if (exitCode != 0 && !ct.IsCancellationRequested)
                    {
                        OnStateChanged(StreamState.Error);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Beklenen
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[StreamController] Monitor hatası");
            }
            finally
            {
                IsRunning = false;
            }
        }

        private void OnFfmpegOutput(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                RaiseLog("debug", $"[FFmpeg] {e.Data}");
            }
        }

        private void OnFfmpegError(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data))
                return;

            // FFmpeg hata çıktısını parse et
            var data = e.Data;

            if (data.Contains("frame=") && data.Contains("fps="))
            {
                // İstatistik satırı
                ParseStatistics(data);
                RaiseLog("debug", $"[FFmpeg Stats] {data}");
            }
            else if (data.Contains("Error") || data.Contains("error"))
            {
                RaiseLog("error", $"[FFmpeg] {data}");
            }
            else if (data.Contains("Warning") || data.Contains("warning"))
            {
                RaiseLog("warning", $"[FFmpeg] {data}");
            }
            else
            {
                RaiseLog("debug", $"[FFmpeg] {data}");
            }
        }

        private void ParseStatistics(string line)
        {
            try
            {
                // frame= 1234 fps= 30 q=28.0 size=    5678kB time=00:01:23.45 bitrate= 500.0kbits/s
                var stats = new StreamStatistics();

                // Frame count
                var frameMatch = System.Text.RegularExpressions.Regex.Match(line, @"frame=\s*(\d+)");
                if (frameMatch.Success)
                    stats.FrameCount = long.Parse(frameMatch.Groups[1].Value);

                // FPS
                var fpsMatch = System.Text.RegularExpressions.Regex.Match(line, @"fps=\s*([\d.]+)");
                if (fpsMatch.Success)
                    stats.CurrentFps = double.Parse(fpsMatch.Groups[1].Value);

                // Bitrate
                var bitrateMatch = System.Text.RegularExpressions.Regex.Match(line, @"bitrate=\s*([\d.]+)");
                if (bitrateMatch.Success)
                    stats.CurrentBitrate = double.Parse(bitrateMatch.Groups[1].Value);

                // Size
                var sizeMatch = System.Text.RegularExpressions.Regex.Match(line, @"size=\s*(\d+)");
                if (sizeMatch.Success)
                    stats.TotalBytes = long.Parse(sizeMatch.Groups[1].Value) * 1024;

                StatisticsUpdated?.Invoke(this, new StreamStatisticsEventArgs(stats));
            }
            catch
            {
                // İstatistik parse hatası - yok say
            }
        }

        private void CleanupProcess()
        {
            try
            {
                if (_ffmpegProcess != null)
                {
                    _ffmpegProcess.OutputDataReceived -= OnFfmpegOutput;
                    _ffmpegProcess.ErrorDataReceived -= OnFfmpegError;
                    _ffmpegProcess.Dispose();
                    _ffmpegProcess = null;
                }
            }
            catch { }
        }

        private string BuildFfmpegArgs(StreamConfiguration config)
        {
            var args = new System.Text.StringBuilder();

            // Input
            args.Append($"-re -i \"{config.InputSource}\" ");

            // Video encoding
            args.Append($"-c:v libx264 -preset {config.Preset} ");
            args.Append($"-b:v {config.VideoBitrate}k ");
            args.Append($"-maxrate {config.VideoBitrate}k -bufsize {config.VideoBitrate * 2}k ");
            args.Append($"-r {config.Fps} ");
            args.Append($"-g {config.Fps * 2} "); // Keyframe interval

            // Audio encoding
            args.Append($"-c:a aac -b:a {config.AudioBitrate}k -ar 44100 ");

            // Output format
            args.Append("-f flv ");

            // Output URL
            args.Append($"\"{config.OutputUrl}\"");

            return args.ToString();
        }

        private static string? FindFfmpegPath()
        {
            // 1. Uygulama dizini
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var localPath = Path.Combine(appDir, "ffmpeg.exe");
            if (File.Exists(localPath))
                return localPath;

            // 2. PATH'de ara
            var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();
            foreach (var dir in pathDirs)
            {
                try
                {
                    var path = Path.Combine(dir, "ffmpeg.exe");
                    if (File.Exists(path))
                        return path;
                }
                catch { }
            }

            return null;
        }

        private void OnStateChanged(StreamState newState)
        {
            try
            {
                StateChanged?.Invoke(this, new StreamStateChangedEventArgs(newState));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[StreamController] StateChanged event hatası");
            }
        }

        private void RaiseLog(string level, string message)
        {
            try
            {
                LogMessage?.Invoke(level, message);
            }
            catch { }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(StreamController));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch { }

            _cts?.Dispose();

            LogMessage = null;
            StateChanged = null;
            StatisticsUpdated = null;
        }

        #endregion
    }

    #region Supporting Types

    public sealed class StreamConfiguration
    {
        public string StreamId { get; set; } = Guid.NewGuid().ToString("N");
        public string InputSource { get; set; } = "";
        public string OutputUrl { get; set; } = "";
        public int VideoBitrate { get; set; } = 2500;
        public int AudioBitrate { get; set; } = 128;
        public int Fps { get; set; } = 30;
        public string Preset { get; set; } = "veryfast";
    }

    public sealed class StreamInfo
    {
        public string Id { get; set; } = "";
        public string OutputUrl { get; set; } = "";
        public DateTime StartedAt { get; set; }
        public int ProcessId { get; set; }
        public TimeSpan Uptime => DateTime.UtcNow - StartedAt;
    }

    public sealed class StreamStatistics
    {
        public long FrameCount { get; set; }
        public double CurrentFps { get; set; }
        public double CurrentBitrate { get; set; }
        public long TotalBytes { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public enum StreamState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Error
    }

    public sealed class StreamStateChangedEventArgs : EventArgs
    {
        public StreamState NewState { get; }
        public DateTime Timestamp { get; }

        public StreamStateChangedEventArgs(StreamState newState)
        {
            NewState = newState;
            Timestamp = DateTime.UtcNow;
        }
    }

    public sealed class StreamStatisticsEventArgs : EventArgs
    {
        public StreamStatistics Statistics { get; }

        public StreamStatisticsEventArgs(StreamStatistics statistics)
        {
            Statistics = statistics;
        }
    }

    #endregion
}