using System;
using System.Collections.Generic;

namespace UniCast.App.Services.Pipeline
{
    /// <summary>
    /// FFmpeg pipeline konfigürasyonu.
    /// Kamera, çıkış boyutları, encoder ayarları ve overlay bilgilerini içerir.
    /// </summary>
    public class PipelineConfig
    {
        #region Kamera Ayarları

        /// <summary>
        /// DirectShow kamera adı (örn: "Brio 100")
        /// </summary>
        public string CameraName { get; set; } = string.Empty;

        /// <summary>
        /// DirectShow mikrofon adı (örn: "Mikrofon (HyperX Cloud III)")
        /// Null ise sessiz audio üretilir.
        /// </summary>
        public string? MicrophoneName { get; set; }

        /// <summary>
        /// Kamera döndürme açısı (0, 90, 180, 270)
        /// </summary>
        public int CameraRotation { get; set; } = 0;

        #endregion

        #region Çıkış Ayarları

        /// <summary>
        /// Çıkış genişliği (preview ve yayın için)
        /// </summary>
        public int OutputWidth { get; set; } = 1920;

        /// <summary>
        /// Çıkış yüksekliği (preview ve yayın için)
        /// </summary>
        public int OutputHeight { get; set; } = 1080;

        /// <summary>
        /// Hedef FPS
        /// </summary>
        public int Fps { get; set; } = 30;

        #endregion

        #region Encoder Ayarları

        /// <summary>
        /// Video bitrate (kbps)
        /// </summary>
        public int VideoBitrate { get; set; } = 4500;

        /// <summary>
        /// Audio bitrate (kbps)
        /// </summary>
        public int AudioBitrate { get; set; } = 128;

        /// <summary>
        /// Video encoder (h264_nvenc, libx264, h264_amf, h264_qsv)
        /// </summary>
        public string VideoEncoder { get; set; } = "h264_nvenc";

        /// <summary>
        /// Encoder preset
        /// NVENC: p1-p7 (p1=fastest, p7=best quality)
        /// x264: ultrafast, superfast, veryfast, faster, fast, medium
        /// </summary>
        public string Preset { get; set; } = "p4";

        /// <summary>
        /// Audio sample rate
        /// </summary>
        public int AudioSampleRate { get; set; } = 44100;

        /// <summary>
        /// Audio channel sayısı
        /// </summary>
        public int AudioChannels { get; set; } = 2;

        #endregion

        #region Chat Overlay Ayarları

        /// <summary>
        /// Chat overlay aktif mi?
        /// </summary>
        public bool EnableChatOverlay { get; set; } = false;

        /// <summary>
        /// Chat overlay pipe adı (örn: "unicast_chat_overlay")
        /// </summary>
        public string? ChatOverlayPipeName { get; set; }

        #endregion

        #region FFmpeg Ayarları

        /// <summary>
        /// FFmpeg executable yolu
        /// </summary>
        public string FfmpegPath { get; set; } = "ffmpeg.exe";

        /// <summary>
        /// DirectShow buffer boyutu
        /// </summary>
        public string RtBufSize { get; set; } = "100M";

        #endregion

        #region Hesaplanmış Değerler

        /// <summary>
        /// Kameranın açılacağı genişlik (rotation'dan önce)
        /// Kameralar dikey çözünürlük desteklemez, her zaman yatay açılır.
        /// </summary>
        public int CameraWidth => Math.Max(OutputWidth, OutputHeight);

        /// <summary>
        /// Kameranın açılacağı yükseklik (rotation'dan önce)
        /// </summary>
        public int CameraHeight => Math.Min(OutputWidth, OutputHeight);

        /// <summary>
        /// Çıkış dikey mi? (Height > Width)
        /// </summary>
        public bool IsVerticalOutput => OutputHeight > OutputWidth;

        /// <summary>
        /// Preview frame boyutu (byte cinsinden, BGR24)
        /// </summary>
        public int PreviewFrameSize => OutputWidth * OutputHeight * 3;

        /// <summary>
        /// Rotation gerektiriyor mu?
        /// </summary>
        public bool RequiresRotation => CameraRotation != 0 || IsVerticalOutput;

        #endregion

        #region Validation

        /// <summary>
        /// Konfigürasyonu doğrula
        /// </summary>
        public ValidationResult Validate()
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(CameraName))
                errors.Add("Kamera adı boş olamaz");

            if (OutputWidth < 320 || OutputWidth > 3840)
                errors.Add($"Geçersiz genişlik: {OutputWidth} (320-3840 arası olmalı)");

            if (OutputHeight < 240 || OutputHeight > 2160)
                errors.Add($"Geçersiz yükseklik: {OutputHeight} (240-2160 arası olmalı)");

            if (Fps < 1 || Fps > 120)
                errors.Add($"Geçersiz FPS: {Fps} (1-120 arası olmalı)");

            if (VideoBitrate < 500 || VideoBitrate > 50000)
                errors.Add($"Geçersiz video bitrate: {VideoBitrate} (500-50000 arası olmalı)");

            if (CameraRotation != 0 && CameraRotation != 90 && CameraRotation != 180 && CameraRotation != 270)
                errors.Add($"Geçersiz rotation: {CameraRotation} (0, 90, 180, 270 olmalı)");

            if (EnableChatOverlay && string.IsNullOrWhiteSpace(ChatOverlayPipeName))
                errors.Add("Chat overlay aktif ama pipe adı belirtilmemiş");

            return new ValidationResult(errors.Count == 0, errors);
        }

        #endregion

        #region Factory Methods

        /// <summary>
        /// SettingsStore'dan PipelineConfig oluştur
        /// </summary>
        public static PipelineConfig FromSettings(
            string cameraName,
            string? microphoneName,
            int width,
            int height,
            int fps,
            int rotation,
            int videoBitrate,
            int audioBitrate,
            string encoder,
            string preset,
            string ffmpegPath)
        {
            return new PipelineConfig
            {
                CameraName = cameraName,
                MicrophoneName = microphoneName,
                OutputWidth = width,
                OutputHeight = height,
                Fps = fps,
                CameraRotation = rotation,
                VideoBitrate = videoBitrate,
                AudioBitrate = audioBitrate,
                VideoEncoder = encoder,
                Preset = preset,
                FfmpegPath = ffmpegPath
            };
        }

        #endregion
    }

    /// <summary>
    /// Stream hedefi (RTMP URL)
    /// </summary>
    public class StreamTarget
    {
        /// <summary>
        /// Platform adı (YouTube, Twitch, TikTok, vb.)
        /// </summary>
        public string Platform { get; set; } = string.Empty;

        /// <summary>
        /// RTMP URL (stream key dahil)
        /// </summary>
        public string RtmpUrl { get; set; } = string.Empty;

        /// <summary>
        /// Bu hedef aktif mi?
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// URL'deki stream key'i maskele (log için)
        /// </summary>
        public string MaskedUrl
        {
            get
            {
                if (string.IsNullOrEmpty(RtmpUrl)) return RtmpUrl;

                try
                {
                    var uri = new Uri(RtmpUrl);
                    var segments = uri.AbsolutePath.Split('/');
                    if (segments.Length > 1)
                    {
                        var lastSegment = segments[^1];
                        if (!string.IsNullOrEmpty(lastSegment) && lastSegment.Length > 4)
                        {
                            var masked = lastSegment[..4] + new string('*', Math.Min(lastSegment.Length - 4, 12));
                            return $"{uri.Scheme}://{uri.Host}{string.Join("/", segments[..^1])}/{masked}";
                        }
                    }
                    return RtmpUrl;
                }
                catch
                {
                    return "rtmp://***";
                }
            }
        }
    }

    /// <summary>
    /// Konfigürasyon doğrulama sonucu
    /// </summary>
    public class ValidationResult
    {
        public bool IsValid { get; }
        public IReadOnlyList<string> Errors { get; }

        public ValidationResult(bool isValid, IReadOnlyList<string> errors)
        {
            IsValid = isValid;
            Errors = errors;
        }

        public static ValidationResult Success() => new(true, Array.Empty<string>());
    }
}