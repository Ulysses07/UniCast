using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
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
        /// GÜVENLİK: Stream URL'deki hassas bilgileri (stream key) maskeler.
        /// Log dosyalarına yazılmadan önce kullanılmalı.
        /// </summary>
        private static string MaskSensitiveUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;

            try
            {
                // RTMP URL formatı: rtmp://server/app/stream_key
                // Örnek: rtmp://a.rtmp.youtube.com/live2/xxxx-xxxx-xxxx-xxxx

                // URL'i parçala
                var uri = new Uri(url);
                var path = uri.AbsolutePath;

                // Path'in son segmentini (stream key) maskele
                var segments = path.Split('/');
                if (segments.Length > 1)
                {
                    var lastSegment = segments[^1];
                    if (!string.IsNullOrEmpty(lastSegment) && lastSegment.Length > 4)
                    {
                        // Stream key'in sadece ilk 4 karakterini göster
                        var masked = lastSegment[..4] + new string('*', Math.Min(lastSegment.Length - 4, 16));
                        segments[^1] = masked;

                        return $"{uri.Scheme}://{uri.Host}{string.Join("/", segments)}";
                    }
                }

                return url;
            }
            catch
            {
                // Parse edilemezse, en azından son 20 karakteri maskele
                if (url.Length > 24)
                {
                    return url[..^20] + new string('*', 16) + "****";
                }
                return "***MASKED***";
            }
        }

        /// <summary>
        /// GÜVENLİK: FFmpeg arguments'daki stream key'leri maskeler.
        /// </summary>
        private static string MaskSensitiveArgs(string args)
        {
            if (string.IsNullOrEmpty(args))
                return args;

            // -f flv "rtmp://..." kısmını bul ve maskele
            var pattern = new System.Text.RegularExpressions.Regex(
                @"(rtmp[s]?://[^/]+/[^/]+/)([^\s""']+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return pattern.Replace(args, m =>
            {
                var streamKey = m.Groups[2].Value;
                if (streamKey.Length > 4)
                {
                    return m.Groups[1].Value + streamKey[..4] + new string('*', Math.Min(12, streamKey.Length - 4));
                }
                return m.Value;
            });
        }

        /// <summary>
        /// Stream'i başlatır.
        /// </summary>
        public Task<bool> StartAsync(StreamConfiguration config, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            if (IsRunning)
            {
                RaiseLog("warning", "Stream zaten çalışıyor");
                return Task.FromResult(false);
            }

            if (string.IsNullOrEmpty(FfmpegPath) || !File.Exists(FfmpegPath))
            {
                RaiseLog("error", "FFmpeg bulunamadı");
                return Task.FromResult(false);
            }

            lock (_stateLock)
            {
                if (_isRunning)
                    return Task.FromResult(false);

                _cts?.Dispose();
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            }

            try
            {
                // GÜVENLİK: Stream URL'i loglarken stream key'i maskele
                RaiseLog("info", $"Stream başlatılıyor: {MaskSensitiveUrl(config.OutputUrl)}");

                var args = BuildFfmpegArgs(config);

                // DEBUG: Tee muxer için gerçek args'ı kontrol et
                if (config.UseTeeMuxer)
                {
                    System.Diagnostics.Debug.WriteLine($"[StreamController] FULL TEE ARGS LENGTH: {args.Length}");
                    System.Diagnostics.Debug.WriteLine($"[StreamController] TEE OUTPUT URL LENGTH: {config.OutputUrl.Length}");
                    System.Diagnostics.Debug.WriteLine($"[StreamController] PIPE COUNT: {args.Count(c => c == '|')}");
                }

                // GÜVENLİK: FFmpeg args'da stream key'i maskele
                RaiseLog("debug", $"FFmpeg args: {MaskSensitiveArgs(args)}");

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
                    return Task.FromResult(false);
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
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                RaiseLog("error", $"Stream başlatma hatası: {ex.Message}");
                Log.Error(ex, "[StreamController] Start hatası");

                CleanupProcess();
                return Task.FromResult(false);
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
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "[StreamController] FFmpeg kapatma hatası");
                        try { _ffmpegProcess.Kill(); } catch (Exception killEx) { Log.Debug(killEx, "[StreamController] FFmpeg kill hatası"); }
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
            catch (Exception ex)
            {
                // DÜZELTME v25: İstatistik parse hatası - loglama eklendi
                System.Diagnostics.Debug.WriteLine($"[StreamController] İstatistik parse hatası: {ex.Message}");
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
            catch (Exception ex) { Log.Debug(ex, "[StreamController] FFmpeg cleanup hatası"); }
        }

        private string BuildFfmpegArgs(StreamConfiguration config)
        {
            var args = new System.Text.StringBuilder();

            // Input source analizi
            var videoSource = config.InputSource ?? "";
            var audioSource = config.AudioSource;
            bool hasAudio = !string.IsNullOrWhiteSpace(audioSource);
            bool hasChatOverlay = config.ChatOverlayEnabled && !string.IsNullOrEmpty(config.ChatOverlayPipeName);

            // INPUT 0: VIDEO
            if (config.UsePipeInput && !string.IsNullOrEmpty(config.PipeName))
            {
                // Named pipe'dan raw video oku (PreviewService'den geliyor)
                // Rotation zaten PreviewService'de uygulandı!
                args.Append($"-f rawvideo -pix_fmt bgr24 ");
                args.Append($"-s {config.Width}x{config.Height} ");
                args.Append($"-r {config.Fps} ");
                args.Append($"-i \"\\\\.\\pipe\\{config.PipeName}\" ");

                System.Diagnostics.Debug.WriteLine($"[StreamController] Pipe input: {config.PipeName}, {config.Width}x{config.Height}@{config.Fps}fps");
            }
            else
            {
                // Windows DirectShow cihazı mı kontrol et
                bool isDirectShowVideo = videoSource.StartsWith("video=") ||
                                          videoSource.Contains("@device");

                // DirectShow video input
                if (isDirectShowVideo)
                {
                    args.Append("-f dshow ");
                    args.Append("-rtbufsize 100M ");

                    // Kamera çözünürlüğü - HER ZAMAN YATAY FORMAT (büyük x küçük)
                    // Kameralar dikey çözünürlük desteklemez, FFmpeg filtreleriyle döndürülür
                    int cameraWidth = Math.Max(config.Width, config.Height);   // Büyük olan (1920)
                    int cameraHeight = Math.Min(config.Width, config.Height);  // Küçük olan (1080)

                    args.Append($"-video_size {cameraWidth}x{cameraHeight} ");
                    args.Append($"-framerate {config.Fps} ");
                    args.Append($"-i \"{videoSource}\" ");

                    System.Diagnostics.Debug.WriteLine($"[StreamController] DirectShow input: {cameraWidth}x{cameraHeight}@{config.Fps}fps (camera always horizontal)");
                }
                else
                {
                    args.Append($"-re -i \"{videoSource}\" ");
                }
            }

            // INPUT 1: AUDIO
            if (hasAudio)
            {
                // DirectShow audio ayrı input olarak
                args.Append("-f dshow ");
                args.Append($"-i audio=\"{audioSource}\" ");
            }
            else
            {
                // Sessiz audio üret (RTMP için gerekli) - süresiz
                args.Append("-f lavfi -i anullsrc=r=44100:cl=stereo ");
            }

            // INPUT 2: CHAT OVERLAY (varsa)
            if (hasChatOverlay)
            {
                args.Append($"-f rawvideo -pix_fmt bgra ");
                args.Append($"-s {config.Width}x{config.Height} ");
                args.Append($"-r {config.Fps} ");
                args.Append($"-i \"\\\\.\\pipe\\{config.ChatOverlayPipeName}\" ");

                System.Diagnostics.Debug.WriteLine($"[StreamController] Chat overlay pipe: {config.ChatOverlayPipeName}");
            }

            // Video encoding - NVENC kullan (varsa), yoksa libx264
            // NVENC çok daha hızlı ve CPU yükü yok
            bool useNvenc = true; // TODO: HardwareEncoder'dan al

            if (useNvenc)
            {
                // NVENC encoder - GPU hızlandırmalı
                args.Append("-c:v h264_nvenc ");
                args.Append("-preset p4 ");  // p1=fastest, p7=slowest, p4=balanced
                args.Append("-tune ll ");    // low latency
                args.Append("-rc cbr ");     // constant bitrate
                args.Append($"-b:v {config.VideoBitrate}k ");
                args.Append($"-maxrate {config.VideoBitrate}k -bufsize {config.VideoBitrate * 2}k ");
                args.Append($"-g {config.Fps * 2} ");
                args.Append("-bf 0 ");       // no B-frames for low latency
            }
            else
            {
                // Software fallback
                args.Append($"-c:v libx264 -preset {config.Preset} ");
                args.Append($"-b:v {config.VideoBitrate}k ");
                args.Append($"-maxrate {config.VideoBitrate}k -bufsize {config.VideoBitrate * 4}k ");
                args.Append("-tune zerolatency ");
                args.Append($"-g {config.Fps * 2} ");
            }

            // Video filtreleri - chat overlay varsa filter_complex kullan
            if (hasChatOverlay)
            {
                // filter_complex ile overlay
                var filterComplex = new System.Text.StringBuilder();

                // Rotation (pipe kullanılmıyorsa)
                string rotationFilter = "";
                if (!config.UsePipeInput)
                {
                    int rotation = config.CameraRotation switch
                    {
                        90 or -270 => 90,
                        180 or -180 => 180,
                        270 or -90 => 270,
                        _ => 0
                    };

                    if (rotation == 90)
                        rotationFilter = "transpose=1,";
                    else if (rotation == 180)
                        rotationFilter = "transpose=1,transpose=1,";
                    else if (rotation == 270)
                        rotationFilter = "transpose=2,";
                }

                // Video işleme: rotation + scale + pad + format
                filterComplex.Append($"[0:v]{rotationFilter}fps={config.Fps},");
                filterComplex.Append($"scale={config.Width}:{config.Height}:force_original_aspect_ratio=decrease,");
                filterComplex.Append($"pad={config.Width}:{config.Height}:(ow-iw)/2:(oh-ih)/2,");
                filterComplex.Append("format=yuva420p[v_main];");

                // Chat overlay'i video üzerine yerleştir
                filterComplex.Append("[v_main][2:v]overlay=0:0:eof_action=pass:format=auto,format=yuv420p[v_out]");

                args.Append($"-filter_complex \"{filterComplex}\" ");
                args.Append("-map \"[v_out]\" ");
            }
            else
            {
                // Basit video filtresi (chat overlay yok)
                var filters = new System.Collections.Generic.List<string>();

                // Rotation filtresi - SADECE pipe kullanılmıyorsa ekle
                if (!config.UsePipeInput)
                {
                    int rotation = config.CameraRotation switch
                    {
                        90 or -270 => 90,
                        180 or -180 => 180,
                        270 or -90 => 270,
                        _ => 0
                    };

                    if (rotation == 90)
                        filters.Add("transpose=1");
                    else if (rotation == 180)
                        filters.Add("transpose=1,transpose=1");
                    else if (rotation == 270)
                        filters.Add("transpose=2");
                }

                filters.Add($"fps={config.Fps}");
                filters.Add($"scale={config.Width}:{config.Height}:force_original_aspect_ratio=decrease");
                filters.Add($"pad={config.Width}:{config.Height}:(ow-iw)/2:(oh-ih)/2");
                filters.Add("format=yuv420p");

                args.Append($"-vf \"{string.Join(",", filters)}\" ");
                args.Append("-map 0:v:0 ");
            }

            // Audio encoding
            args.Append($"-c:a aac -b:a {config.AudioBitrate}k -ar 44100 -ac 2 ");

            // Audio mapping
            args.Append("-map 1:a:0 ");

            // Tee muxer kullanılıyorsa özel output format
            if (config.UseTeeMuxer && config.OutputUrl.StartsWith("tee:"))
            {
                // Tee muxer için -f tee kullan
                args.Append("-f tee ");

                // tee: prefix'ini kaldır
                var teeOutputs = config.OutputUrl.Substring(4);  // "tee:" kısmını kaldır

                // Windows'ta pipe karakteri özel karakter, escape etmeye gerek yok
                // ama tırnak içinde olmalı
                args.Append($"\"{teeOutputs}\"");
            }
            else
            {
                // Normal tek output
                // RTMP flags
                args.Append("-flvflags no_duration_filesize ");
                args.Append("-f flv ");

                // Output URL
                args.Append($"\"{config.OutputUrl}\"");
            }

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
                catch (Exception ex) { Log.Debug(ex, "[StreamController] FFmpeg arama hatası ({Dir})", dir); }
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
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[StreamController] LogMessage event hatası: {ex.Message}"); }
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
            catch (Exception ex) { Log.Debug(ex, "[StreamController] Dispose sırasında stop hatası"); }

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
        public string? AudioSource { get; set; }  // Ses kaynağı (mikrofon)
        public string OutputUrl { get; set; } = "";
        public int VideoBitrate { get; set; } = 2500;
        public int AudioBitrate { get; set; } = 128;
        public int Fps { get; set; } = 30;
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public string Preset { get; set; } = "veryfast";
        public bool UseTeeMuxer { get; set; } = false;  // Multi-target için tee muxer kullan
        public int CameraRotation { get; set; } = 0;  // Kamera döndürme açısı (0, 90, 180, 270)
        public bool UsePipeInput { get; set; } = false;  // Preview'dan pipe ile video al
        public string? PipeName { get; set; }  // Named pipe adı

        // Chat Overlay
        public bool ChatOverlayEnabled { get; set; } = false;
        public string? ChatOverlayPipeName { get; set; }
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