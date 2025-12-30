using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;
using UniCast.Encoder;
using Application = System.Windows.Application; // FfmpegArgsBuilder için

namespace UniCast.App.Services.Pipeline
{
    /// <summary>
    /// FFmpeg tabanlı video pipeline.
    /// Tek FFmpeg process ile hem preview hem yayın yönetir.
    /// OBS tarzı "tek kaynak, çoklu çıktı" mimarisi.
    /// </summary>
    public sealed class FFmpegPipeline : IDisposable
    {
        #region Enums

        /// <summary>
        /// Pipeline durumu
        /// </summary>
        public enum PipelineState
        {
            /// <summary>FFmpeg çalışmıyor</summary>
            Stopped,

            /// <summary>Başlatılıyor</summary>
            Starting,

            /// <summary>Sadece preview aktif</summary>
            PreviewOnly,

            /// <summary>Preview + yayın aktif</summary>
            Streaming,

            /// <summary>Durduruluyor</summary>
            Stopping,

            /// <summary>Hata durumu</summary>
            Error
        }

        #endregion

        #region Events

        /// <summary>Preview frame hazır olduğunda tetiklenir</summary>
        public event Action<ImageSource>? OnPreviewFrame;

        /// <summary>Pipeline durumu değiştiğinde tetiklenir</summary>
        public event Action<PipelineState>? OnStateChanged;

        /// <summary>Hata oluştuğunda tetiklenir</summary>
        public event Action<string>? OnError;

        /// <summary>FFmpeg log mesajı geldiğinde tetiklenir</summary>
        public event Action<string>? OnLogMessage;

        /// <summary>İstatistik güncellendiğinde tetiklenir (fps, bitrate, vb.)</summary>
        public event Action<PipelineStatistics>? OnStatistics;

        #endregion

        #region Properties

        /// <summary>Mevcut pipeline durumu</summary>
        public PipelineState State { get; private set; } = PipelineState.Stopped;

        /// <summary>Preview çalışıyor mu?</summary>
        public bool IsPreviewRunning => State == PipelineState.PreviewOnly || State == PipelineState.Streaming;

        /// <summary>Yayın aktif mi?</summary>
        public bool IsStreamRunning => State == PipelineState.Streaming;

        /// <summary>Aktif konfigürasyon</summary>
        public PipelineConfig? CurrentConfig { get; private set; }

        /// <summary>Aktif stream hedefleri</summary>
        public IReadOnlyList<StreamTarget>? CurrentTargets { get; private set; }

        #endregion

        #region Private Fields

        private Process? _ffmpegProcess;
        private CancellationTokenSource? _cts;
        private Task? _frameReaderTask;
        private Task? _stderrReaderTask;

        // WriteableBitmap (UI thread'de oluşturulmalı)
        private WriteableBitmap? _previewBitmap;
        private byte[]? _frameBuffer;
        private readonly Dispatcher _dispatcher;

        // State management
        private readonly SemaphoreSlim _stateLock = new(1, 1);
        private int _restartCount = 0;
        private const int MaxRestartAttempts = 3;

        // Statistics
        private readonly Stopwatch _statsTimer = new();
        private long _framesReceived;
        private long _bytesReceived;

        private bool _disposed;

        #endregion

        #region Constructor

        /// <summary>
        /// FFmpegPipeline oluştur
        /// </summary>
        /// <param name="dispatcher">WPF UI dispatcher (WriteableBitmap için)</param>
        public FFmpegPipeline(Dispatcher? dispatcher = null)
        {
            _dispatcher = dispatcher ?? Application.Current?.Dispatcher
                ?? throw new InvalidOperationException("UI Dispatcher bulunamadı");
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Sadece preview başlat
        /// </summary>
        public async Task StartPreviewAsync(PipelineConfig config, CancellationToken ct = default)
        {
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (State != PipelineState.Stopped && State != PipelineState.Error)
                {
                    Log.Warning("[FFmpegPipeline] StartPreview çağrıldı ama state={State}", State);
                    return;
                }

                // Validate config
                var validation = config.Validate();
                if (!validation.IsValid)
                {
                    var errorMsg = string.Join(", ", validation.Errors);
                    Log.Error("[FFmpegPipeline] Konfigürasyon hatası: {Errors}", errorMsg);
                    OnError?.Invoke($"Konfigürasyon hatası: {errorMsg}");
                    return;
                }

                CurrentConfig = config;
                CurrentTargets = null;
                _restartCount = 0;

                await StartFFmpegAsync(BuildPreviewOnlyArgs(config), ct).ConfigureAwait(false);

                if (State == PipelineState.Starting)
                {
                    SetState(PipelineState.PreviewOnly);
                }
            }
            finally
            {
                _stateLock.Release();
            }
        }

        /// <summary>
        /// Preview + yayın başlat
        /// </summary>
        public async Task StartStreamAsync(List<StreamTarget> targets, CancellationToken ct = default)
        {
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (CurrentConfig == null)
                {
                    OnError?.Invoke("Önce StartPreviewAsync çağrılmalı");
                    return;
                }

                var enabledTargets = targets.FindAll(t => t.Enabled);
                if (enabledTargets.Count == 0)
                {
                    OnError?.Invoke("En az bir aktif stream hedefi gerekli");
                    return;
                }

                CurrentTargets = enabledTargets;
                _restartCount = 0;

                // Mevcut FFmpeg'i durdur
                await StopFFmpegInternalAsync().ConfigureAwait(false);

                // Yeni args ile başlat
                var args = BuildPreviewAndStreamArgs(CurrentConfig, enabledTargets);
                await StartFFmpegAsync(args, ct).ConfigureAwait(false);

                if (State == PipelineState.Starting)
                {
                    SetState(PipelineState.Streaming);
                    Log.Information("[FFmpegPipeline] Yayın başladı - {Count} platform", enabledTargets.Count);
                }
            }
            finally
            {
                _stateLock.Release();
            }
        }

        /// <summary>
        /// Yayını durdur, preview devam etsin
        /// </summary>
        public async Task StopStreamAsync(CancellationToken ct = default)
        {
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (State != PipelineState.Streaming)
                {
                    return;
                }

                if (CurrentConfig == null)
                {
                    await StopAllAsync(ct).ConfigureAwait(false);
                    return;
                }

                CurrentTargets = null;

                // FFmpeg'i yeniden başlat (sadece preview)
                await StopFFmpegInternalAsync().ConfigureAwait(false);
                await StartFFmpegAsync(BuildPreviewOnlyArgs(CurrentConfig), ct).ConfigureAwait(false);

                if (State == PipelineState.Starting)
                {
                    SetState(PipelineState.PreviewOnly);
                    Log.Information("[FFmpegPipeline] Yayın durduruldu, preview devam ediyor");
                }
            }
            finally
            {
                _stateLock.Release();
            }
        }

        /// <summary>
        /// Her şeyi durdur
        /// </summary>
        public async Task StopAllAsync(CancellationToken ct = default)
        {
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await StopFFmpegInternalAsync().ConfigureAwait(false);
                CurrentConfig = null;
                CurrentTargets = null;
                SetState(PipelineState.Stopped);
                Log.Information("[FFmpegPipeline] Pipeline durduruldu");
            }
            finally
            {
                _stateLock.Release();
            }
        }

        #endregion

        #region FFmpeg Process Management

        private async Task StartFFmpegAsync(string args, CancellationToken ct)
        {
            SetState(PipelineState.Starting);

            var config = CurrentConfig!;
            var ffmpegPath = config.FfmpegPath;

            if (!File.Exists(ffmpegPath))
            {
                // Uygulama klasöründe ara
                var appPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
                if (File.Exists(appPath))
                {
                    ffmpegPath = appPath;
                }
                else
                {
                    SetState(PipelineState.Error);
                    OnError?.Invoke("FFmpeg bulunamadı");
                    return;
                }
            }

            Log.Debug("[FFmpegPipeline] FFmpeg başlatılıyor: {Args}",
                FfmpegArgsBuilder.MaskStreamKeys(args));

            _cts = new CancellationTokenSource();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,  // Preview frames
                    RedirectStandardError = true,   // FFmpeg logs
                    RedirectStandardInput = true    // Graceful shutdown için 'q'
                };

                _ffmpegProcess = new Process { StartInfo = psi };
                _ffmpegProcess.Start();

                // Frame okuma task'ı başlat
                _frameReaderTask = ReadFramesAsync(
                    _ffmpegProcess.StandardOutput.BaseStream,
                    config,
                    linkedCts.Token);

                // stderr okuma task'ı başlat (log için)
                _stderrReaderTask = ReadStderrAsync(
                    _ffmpegProcess.StandardError,
                    linkedCts.Token);

                // İstatistik timer'ı başlat
                _statsTimer.Restart();
                _framesReceived = 0;
                _bytesReceived = 0;

                Log.Information("[FFmpegPipeline] FFmpeg başlatıldı - PID: {PID}", _ffmpegProcess.Id);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FFmpegPipeline] FFmpeg başlatma hatası");
                SetState(PipelineState.Error);
                OnError?.Invoke($"FFmpeg başlatılamadı: {ex.Message}");
            }
        }

        private async Task StopFFmpegInternalAsync()
        {
            if (_ffmpegProcess == null) return;

            SetState(PipelineState.Stopping);

            // CTS'i iptal et
            try { _cts?.Cancel(); } catch { }

            // Graceful shutdown: 'q' gönder
            try
            {
                if (!_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.StandardInput.Write("q");
                    _ffmpegProcess.StandardInput.Flush();
                }
            }
            catch { }

            // 3 saniye bekle
            try
            {
                var exitTask = Task.Run(() => _ffmpegProcess.WaitForExit(3000));
                await exitTask.ConfigureAwait(false);
            }
            catch { }

            // Hala çalışıyorsa kill
            try
            {
                if (!_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.Kill(true);
                    Log.Warning("[FFmpegPipeline] FFmpeg zorla sonlandırıldı");
                }
            }
            catch { }

            // Task'ları bekle
            try
            {
                if (_frameReaderTask != null)
                    await _frameReaderTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch { }

            try
            {
                if (_stderrReaderTask != null)
                    await _stderrReaderTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch { }

            // Cleanup
            try { _ffmpegProcess?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }

            _ffmpegProcess = null;
            _cts = null;
            _frameReaderTask = null;
            _stderrReaderTask = null;

            _statsTimer.Stop();
        }

        #endregion

        #region Frame Reading

        private async Task ReadFramesAsync(Stream stdout, PipelineConfig config, CancellationToken ct)
        {
            int frameSize = config.PreviewFrameSize;

            // Buffer'ları hazırla
            if (_frameBuffer == null || _frameBuffer.Length != frameSize)
            {
                _frameBuffer = new byte[frameSize];
            }

            // WriteableBitmap'i UI thread'de oluştur
            await _dispatcher.InvokeAsync(() =>
            {
                if (_previewBitmap == null ||
                    _previewBitmap.PixelWidth != config.OutputWidth ||
                    _previewBitmap.PixelHeight != config.OutputHeight)
                {
                    _previewBitmap = new WriteableBitmap(
                        config.OutputWidth,
                        config.OutputHeight,
                        96, 96,
                        PixelFormats.Bgr24,
                        null);

                    Log.Debug("[FFmpegPipeline] WriteableBitmap oluşturuldu: {W}x{H}",
                        config.OutputWidth, config.OutputHeight);
                }
            });

            Log.Debug("[FFmpegPipeline] Frame okuma başladı - FrameSize: {Size} bytes", frameSize);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Frame'i oku
                    int bytesRead = 0;
                    while (bytesRead < frameSize && !ct.IsCancellationRequested)
                    {
                        int read = await stdout.ReadAsync(
                            _frameBuffer, bytesRead, frameSize - bytesRead, ct).ConfigureAwait(false);

                        if (read == 0)
                        {
                            // EOF - FFmpeg kapandı
                            Log.Warning("[FFmpegPipeline] stdout EOF - FFmpeg kapandı");
                            return;
                        }

                        bytesRead += read;
                    }

                    if (bytesRead == frameSize)
                    {
                        _framesReceived++;
                        _bytesReceived += frameSize;

                        // WriteableBitmap'i güncelle (UI thread)
                        var buffer = _frameBuffer;
                        await _dispatcher.InvokeAsync(() =>
                        {
                            UpdatePreviewBitmap(buffer);
                        }, DispatcherPriority.Render);

                        // İstatistik güncelle (her 30 frame'de bir)
                        if (_framesReceived % 30 == 0)
                        {
                            UpdateStatistics();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal kapatma
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FFmpegPipeline] Frame okuma hatası");
                OnError?.Invoke($"Frame okuma hatası: {ex.Message}");
            }

            Log.Debug("[FFmpegPipeline] Frame okuma sonlandı - Toplam: {Frames} frame", _framesReceived);
        }

        private void UpdatePreviewBitmap(byte[] frameData)
        {
            if (_previewBitmap == null) return;

            try
            {
                _previewBitmap.Lock();
                try
                {
                    Marshal.Copy(frameData, 0, _previewBitmap.BackBuffer, frameData.Length);
                    _previewBitmap.AddDirtyRect(new Int32Rect(
                        0, 0, _previewBitmap.PixelWidth, _previewBitmap.PixelHeight));
                }
                finally
                {
                    _previewBitmap.Unlock();
                }

                OnPreviewFrame?.Invoke(_previewBitmap);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FFmpegPipeline] WriteableBitmap güncelleme hatası");
            }
        }

        #endregion

        #region Stderr Reading (FFmpeg Logs)

        private async Task ReadStderrAsync(StreamReader stderr, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await stderr.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line == null) break;

                    // Log seviyesini belirle
                    if (line.Contains("[error]", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Error("[FFmpeg] {Line}", line);
                    }
                    else if (line.Contains("[warning]", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Warning("[FFmpeg] {Line}", line);
                    }
                    else
                    {
                        Log.Debug("[FFmpeg] {Line}", line);
                    }

                    OnLogMessage?.Invoke(line);

                    // Kritik hata kontrolü
                    if (line.Contains("Could not set video options") ||
                        line.Contains("Error opening input") ||
                        line.Contains("Connection refused"))
                    {
                        OnError?.Invoke(line);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error(ex, "[FFmpegPipeline] stderr okuma hatası");
            }
        }

        #endregion

        #region Statistics

        private void UpdateStatistics()
        {
            if (!_statsTimer.IsRunning) return;

            var elapsed = _statsTimer.Elapsed.TotalSeconds;
            if (elapsed < 0.1) return;

            var stats = new PipelineStatistics
            {
                FramesReceived = _framesReceived,
                Fps = _framesReceived / elapsed,
                BytesReceived = _bytesReceived,
                BitrateKbps = (_bytesReceived * 8 / 1000.0) / elapsed,
                Uptime = _statsTimer.Elapsed,
                State = State
            };

            OnStatistics?.Invoke(stats);
        }

        #endregion

        #region State Management

        private void SetState(PipelineState newState)
        {
            if (State == newState) return;

            var oldState = State;
            State = newState;

            Log.Debug("[FFmpegPipeline] State: {Old} → {New}", oldState, newState);
            OnStateChanged?.Invoke(newState);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                // Senkron dispose (async metod çağıramayız)
                _cts?.Cancel();

                try { _ffmpegProcess?.Kill(true); } catch { }
                try { _ffmpegProcess?.Dispose(); } catch { }
                try { _cts?.Dispose(); } catch { }
                try { _stateLock.Dispose(); } catch { }
            }
            catch { }

            OnPreviewFrame = null;
            OnStateChanged = null;
            OnError = null;
            OnLogMessage = null;
            OnStatistics = null;

            _previewBitmap = null;
            _frameBuffer = null;

            GC.SuppressFinalize(this);
        }

        #endregion

        #region FFmpeg Args Helpers

        /// <summary>
        /// PipelineConfig'den preview-only FFmpeg argümanları oluştur
        /// </summary>
        private static string BuildPreviewOnlyArgs(PipelineConfig config)
        {
            return FfmpegArgsBuilder.BuildPreviewOnlyArgs(
                cameraName: config.CameraName,
                outputWidth: config.OutputWidth,
                outputHeight: config.OutputHeight,
                fps: config.Fps,
                cameraRotation: config.CameraRotation);
        }

        /// <summary>
        /// PipelineConfig ve StreamTarget listesinden preview+stream FFmpeg argümanları oluştur
        /// </summary>
        private static string BuildPreviewAndStreamArgs(PipelineConfig config, List<StreamTarget> targets)
        {
            var rtmpUrls = targets.Where(t => t.Enabled).Select(t => t.RtmpUrl).ToList();

            if (rtmpUrls.Count == 0)
                return BuildPreviewOnlyArgs(config);

            if (rtmpUrls.Count == 1)
            {
                return FfmpegArgsBuilder.BuildPreviewAndStreamArgs(
                    cameraName: config.CameraName,
                    audioDeviceName: config.MicrophoneName,
                    outputWidth: config.OutputWidth,
                    outputHeight: config.OutputHeight,
                    fps: config.Fps,
                    videoBitrateKbps: config.VideoBitrate,
                    audioBitrateKbps: config.AudioBitrate,
                    rtmpUrl: rtmpUrls[0],
                    encoderName: config.VideoEncoder,
                    cameraRotation: config.CameraRotation,
                    chatOverlayPipeName: config.EnableChatOverlay ? config.ChatOverlayPipeName : null);
            }
            else
            {
                return FfmpegArgsBuilder.BuildPreviewAndMultiStreamArgs(
                    cameraName: config.CameraName,
                    audioDeviceName: config.MicrophoneName,
                    outputWidth: config.OutputWidth,
                    outputHeight: config.OutputHeight,
                    fps: config.Fps,
                    videoBitrateKbps: config.VideoBitrate,
                    audioBitrateKbps: config.AudioBitrate,
                    rtmpUrls: rtmpUrls,
                    encoderName: config.VideoEncoder,
                    cameraRotation: config.CameraRotation,
                    chatOverlayPipeName: config.EnableChatOverlay ? config.ChatOverlayPipeName : null);
            }
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Pipeline istatistikleri
    /// </summary>
    public class PipelineStatistics
    {
        public long FramesReceived { get; set; }
        public double Fps { get; set; }
        public long BytesReceived { get; set; }
        public double BitrateKbps { get; set; }
        public TimeSpan Uptime { get; set; }
        public FFmpegPipeline.PipelineState State { get; set; }

        public override string ToString()
        {
            return $"FPS: {Fps:F1}, Bitrate: {BitrateKbps:F0} kbps, Frames: {FramesReceived}, Uptime: {Uptime:hh\\:mm\\:ss}";
        }
    }

    #endregion
}