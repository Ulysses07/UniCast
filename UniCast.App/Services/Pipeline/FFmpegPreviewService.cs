using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Serilog;
using UniCast.App.Services.Pipeline;
using UniCast.Core.Models;
using UniCast.Core.Streaming;

// Alias'lar
using SettingsData = UniCast.App.Services.SettingsData;
using StreamTarget = UniCast.App.Services.Pipeline.StreamTarget;

namespace UniCast.App.Services
{
    /// <summary>
    /// FFmpegPipeline için IPreviewService uyumlu wrapper.
    /// Eski OpenCV tabanlı PreviewService yerine kullanılabilir.
    /// Artık yayın yönetimi de bu sınıf üzerinden yapılıyor.
    /// </summary>
    public sealed class FFmpegPreviewService : IPreviewService
    {
        #region Fields

        private readonly FFmpegPipeline _pipeline;
        private PipelineConfig? _config;
        private string? _cameraName;

        private bool _disposed;

        // Status tracking
        private string? _lastMessage = "Hazır";
        private string? _lastMetric;
        private string? _lastAdvisory;
        private bool _isReconnecting;
        private DateTime _streamStartTime;

        #endregion

        #region Status Properties (ControlViewModel entegrasyonu için)

        /// <summary>Son durum mesajı</summary>
        public string? LastMessage => _lastMessage;

        /// <summary>Son metrik (FPS, Bitrate)</summary>
        public string? LastMetric => _lastMetric;

        /// <summary>Son uyarı/hata mesajı</summary>
        public string? LastAdvisory => _lastAdvisory;

        /// <summary>Yeniden bağlanma deneniyorsa true</summary>
        public bool IsReconnecting => _isReconnecting;

        #endregion

        #region IPreviewService Implementation

        public event Action<ImageSource>? OnFrame;

        public bool IsRunning => _pipeline.IsPreviewRunning;
        public bool IsStreaming => _pipeline.IsStreamRunning;

        /// <summary>Kameradan alınan gerçek genişlik</summary>
        public int ActualWidth => _config?.OutputWidth ?? 0;

        /// <summary>Kameradan alınan gerçek yükseklik</summary>
        public int ActualHeight => _config?.OutputHeight ?? 0;

        #endregion

        #region Constructor

        public FFmpegPreviewService()
        {
            _pipeline = new FFmpegPipeline();
            _pipeline.OnPreviewFrame += frame => OnFrame?.Invoke(frame);
            _pipeline.OnError += error =>
            {
                Log.Error("[FFmpegPreviewService] {Error}", error);
                _lastAdvisory = error;
            };
            _pipeline.OnStateChanged += OnPipelineStateChanged;
            _pipeline.OnStatistics += OnPipelineStatistics;
        }

        private void OnPipelineStateChanged(FFmpegPipeline.PipelineState state)
        {
            Log.Debug("[FFmpegPreviewService] State: {State}", state);

            switch (state)
            {
                case FFmpegPipeline.PipelineState.Starting:
                    _lastMessage = "Başlatılıyor...";
                    _isReconnecting = false;
                    break;
                case FFmpegPipeline.PipelineState.PreviewOnly:
                    _lastMessage = "Preview aktif";
                    _isReconnecting = false;
                    break;
                case FFmpegPipeline.PipelineState.Streaming:
                    _lastMessage = "Yayında";
                    _isReconnecting = false;
                    _streamStartTime = DateTime.Now;
                    break;
                case FFmpegPipeline.PipelineState.Stopping:
                    _lastMessage = "Durduruluyor...";
                    break;
                case FFmpegPipeline.PipelineState.Stopped:
                    _lastMessage = "Durduruldu";
                    _lastMetric = null;
                    _isReconnecting = false;
                    break;
                case FFmpegPipeline.PipelineState.Error:
                    _lastMessage = "Hata";
                    _isReconnecting = true; // Yeniden bağlanma denenebilir
                    break;
            }
        }

        private void OnPipelineStatistics(PipelineStatistics stats)
        {
            // Streaming modunda config'deki bitrate'i göster (RTMP'ye giden gerçek değer)
            // Preview modunda sadece FPS göster (raw bitrate kullanıcıyı yanıltır)
            if (_pipeline.IsStreamRunning && _config != null)
            {
                _lastMetric = $"FPS: {stats.Fps:F1} | Bitrate: {_config.VideoBitrate} kbps";
            }
            else
            {
                _lastMetric = $"FPS: {stats.Fps:F1}";
            }
        }

        #endregion

        #region IPreviewService Methods

        /// <summary>
        /// Kamera preview başlat (FFmpeg ile)
        /// </summary>
        public async Task StartAsync(int cameraIndex, int width, int height, int fps, int rotation = 0)
        {
            if (_disposed) return;

            // Kamera adını al (DeviceService'den)
            _cameraName = await GetCameraNameAsync(cameraIndex);
            if (string.IsNullOrEmpty(_cameraName))
            {
                Log.Error("[FFmpegPreviewService] Kamera bulunamadı: index={Index}", cameraIndex);
                return;
            }

            // Konfigürasyon oluştur
            _config = new PipelineConfig
            {
                CameraName = _cameraName,
                MicrophoneName = null, // Preview için mikrofon gerekmez
                OutputWidth = width,
                OutputHeight = height,
                Fps = fps,
                CameraRotation = rotation,
                FfmpegPath = GetFfmpegPath()
            };

            Log.Information("[FFmpegPreviewService] Preview başlatılıyor: {Camera} {W}x{H}@{FPS}fps rotation={R}°",
                _cameraName, width, height, fps, rotation);

            await _pipeline.StartPreviewAsync(_config);
        }

        /// <summary>
        /// Preview durdur
        /// </summary>
        public async Task StopAsync()
        {
            if (_disposed) return;

            Log.Debug("[FFmpegPreviewService] Preview durduruluyor...");
            await _pipeline.StopAllAsync();
        }

        /// <summary>
        /// Yayın başlat (bu metod artık StreamControllerAdapter'dan çağrılmayacak)
        /// </summary>
        public Task StartStreamingAsync(CancellationToken ct = default)
        {
            // Yeni mimaride bu metod kullanılmıyor
            // Stream başlatma doğrudan FFmpegPipeline.StartStreamAsync ile yapılıyor
            Log.Warning("[FFmpegPreviewService] StartStreamingAsync çağrıldı ama yeni mimaride kullanılmıyor");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Yayını durdur
        /// </summary>
        public void StopStreaming()
        {
            // Yeni mimaride bu metod kullanılmıyor
            Log.Warning("[FFmpegPreviewService] StopStreaming çağrıldı ama yeni mimaride kullanılmıyor");
        }

        /// <summary>
        /// Pipe adını al (eski mimari için, yeni mimaride kullanılmıyor)
        /// </summary>
        public string? GetPipeName() => null;

        #endregion

        #region New API Methods

        /// <summary>
        /// Yayın başlat (yeni API)
        /// </summary>
        public async Task StartStreamAsync(
            string microphoneName,
            System.Collections.Generic.List<StreamTarget> targets,
            int videoBitrate = 4500,
            int audioBitrate = 128,
            string encoder = "h264_nvenc",
            string preset = "p4",
            bool enableChatOverlay = false,
            string? chatOverlayPipeName = null,
            CancellationToken ct = default)
        {
            if (_disposed) return;
            if (_config == null)
            {
                Log.Error("[FFmpegPreviewService] StartStreamAsync: Önce StartAsync çağrılmalı");
                return;
            }

            // Config'i güncelle
            _config.MicrophoneName = microphoneName;
            _config.VideoBitrate = videoBitrate;
            _config.AudioBitrate = audioBitrate;
            _config.VideoEncoder = encoder;
            _config.Preset = preset;
            _config.EnableChatOverlay = enableChatOverlay;
            _config.ChatOverlayPipeName = chatOverlayPipeName;

            Log.Information("[FFmpegPreviewService] Yayın başlatılıyor: {Count} platform, encoder={Encoder}",
                targets.Count, encoder);

            await _pipeline.StartStreamAsync(targets, ct);
        }

        /// <summary>
        /// Yayın başlat ve sonuç döndür (ControlViewModel entegrasyonu için)
        /// </summary>
        public async Task<StreamStartResult> StartStreamWithResultAsync(
            System.Collections.Generic.List<TargetItem> targetItems,
            SettingsData settings,
            CancellationToken ct = default)
        {
            if (_disposed)
                return StreamStartResult.Fail(StreamErrorCode.Unknown, "Service disposed");

            if (_config == null)
                return StreamStartResult.Fail(StreamErrorCode.InvalidConfig, "Önce preview başlatılmalı (StartAsync)");

            try
            {
                // TargetItem'ları StreamTarget'a dönüştür
                var targets = targetItems
                    .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Url))
                    .Select(t => new StreamTarget
                    {
                        Platform = t.Platform.ToString(),
                        RtmpUrl = BuildRtmpUrl(t),
                        Enabled = true
                    })
                    .ToList();

                if (targets.Count == 0)
                    return StreamStartResult.Fail(StreamErrorCode.InvalidConfig,
                        "Aktif yayın hedefi bulunamadı. En az bir hedef ekleyin ve etkinleştirin.");

                // Mikrofon adını al
                string? microphoneName = settings.SelectedMicrophone;
                if (string.IsNullOrWhiteSpace(microphoneName))
                {
                    // İlk mikrofonu bul
                    try
                    {
                        var deviceService = new Capture.DeviceService();
                        var mics = await deviceService.GetAudioDevicesAsync();
                        microphoneName = mics.Count > 0 ? mics[0].Name : null;
                    }
                    catch { }
                }

                // Config'i güncelle
                _config.MicrophoneName = microphoneName;
                _config.VideoBitrate = settings.VideoKbps;
                _config.AudioBitrate = settings.AudioKbps;
                _config.VideoEncoder = settings.VideoEncoder ?? "h264_nvenc";
                _config.Preset = "p4";
                _config.EnableChatOverlay = settings.StreamChatOverlayEnabled;
                _config.ChatOverlayPipeName = settings.StreamChatOverlayEnabled ? "unicast_chat_overlay" : null;

                Log.Information("[FFmpegPreviewService] Yayın başlatılıyor: {Count} platform, ChatOverlay={ChatEnabled}",
                    targets.Count, settings.StreamChatOverlayEnabled);

                if (settings.StreamChatOverlayEnabled)
                {
                    Log.Information("[FFmpegPreviewService] Chat overlay aktif - Pipe: unicast_chat_overlay");
                }

                foreach (var t in targets)
                {
                    Log.Debug("[FFmpegPreviewService] Hedef: {Platform} -> {Url}", t.Platform, t.MaskedUrl);
                }

                // Yayını başlat
                await _pipeline.StartStreamAsync(targets, ct);

                // Başarılı olup olmadığını kontrol et
                if (_pipeline.IsStreamRunning)
                {
                    return StreamStartResult.Ok();
                }
                else
                {
                    return StreamStartResult.Fail(StreamErrorCode.Unknown, "Yayın başlatılamadı");
                }
            }
            catch (OperationCanceledException)
            {
                return StreamStartResult.Fail(StreamErrorCode.Unknown, "İşlem iptal edildi");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FFmpegPreviewService] Yayın başlatma hatası");
                return StreamStartResult.Fail(StreamErrorCode.Unknown, ex.Message, ex.ToString());
            }
        }

        /// <summary>
        /// RTMP URL oluştur (URL + StreamKey)
        /// </summary>
        private static string BuildRtmpUrl(TargetItem target)
        {
            var url = target.Url ?? "";
            var streamKey = target.StreamKey ?? "";

            if (string.IsNullOrWhiteSpace(streamKey))
                return url;

            // Key zaten URL'de varsa ekleme
            if (url.Contains(streamKey))
                return url;

            if (!url.EndsWith("/"))
                url += "/";

            return url + streamKey;
        }

        /// <summary>
        /// Yayını durdur, preview devam etsin (yeni API)
        /// </summary>
        public async Task StopStreamOnlyAsync(CancellationToken ct = default)
        {
            if (_disposed) return;
            await _pipeline.StopStreamAsync(ct);
        }

        /// <summary>
        /// Pipeline'a doğrudan erişim
        /// </summary>
        public FFmpegPipeline Pipeline => _pipeline;

        #endregion

        #region Helper Methods

        private async Task<string?> GetCameraNameAsync(int cameraIndex)
        {
            try
            {
                // DeviceService'den kamera listesini al
                var deviceService = new Capture.DeviceService();
                var cameras = await deviceService.GetVideoDevicesAsync();

                if (cameraIndex >= 0 && cameraIndex < cameras.Count)
                {
                    return cameras[cameraIndex].Name;
                }

                if (cameras.Count > 0)
                {
                    return cameras[0].Name;
                }

                return null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FFmpegPreviewService] Kamera listesi alınamadı");
                return null;
            }
        }

        private static string GetFfmpegPath()
        {
            var appPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");

            if (System.IO.File.Exists(appPath))
            {
                return appPath;
            }

            // PATH'de ara
            return "ffmpeg.exe";
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            OnFrame = null;
            _pipeline.Dispose();

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}