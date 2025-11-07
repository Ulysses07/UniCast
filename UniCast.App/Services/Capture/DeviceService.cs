using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace UniCast.App.Services.Capture
{
    /// <summary>
    /// WinRT (Windows.Devices.Enumeration) ile kamera/mikrofonları listeler
    /// ve FFmpeg için dshow tabanlı giriş argümanı üretir.
    /// </summary>
    public sealed class DeviceService : IDeviceService
    {
        public async Task<IReadOnlyList<string>> GetVideoFriendlyNamesAsync()
        {
            try
            {
                var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
                // Bazı sistemlerde birden fazla aynı isim dönebiliyor; benzersizleştiriyoruz.
                return devices.Select(d => d.Name)
                              .Where(n => !string.IsNullOrWhiteSpace(n))
                              .Distinct()
                              .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public async Task<IReadOnlyList<string>> GetAudioFriendlyNamesAsync()
        {
            try
            {
                var selector = MediaDevice.GetAudioCaptureSelector();
                var devices = await DeviceInformation.FindAllAsync(selector);
                return devices.Select(d => d.Name)
                              .Where(n => !string.IsNullOrWhiteSpace(n))
                              .Distinct()
                              .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// dshow tabanlı FFmpeg giriş argümanı:
        /// -f dshow -i video="NAME":audio="NAME"
        /// Not: Çözünürlük/FPS’i capture’da zorlamak yerine encoder tarafında scale/fps filter ile ayarlamak genelde daha stabil.
        /// </summary>
        public string BuildFfmpegInputArgs(string? videoFriendlyName, string? audioFriendlyName, int? width = null, int? height = null, int? fps = null)
        {
            var hasVideo = !string.IsNullOrWhiteSpace(videoFriendlyName);
            var hasAudio = !string.IsNullOrWhiteSpace(audioFriendlyName);

            if (!hasVideo && !hasAudio)
                return string.Empty;

            string input = hasVideo && hasAudio
                ? $"video=\"{videoFriendlyName}\"\\:audio=\"{audioFriendlyName}\"" // ':' karakteri bazı cmd ortamlarda kaçış isteyebilir
                : hasVideo
                    ? $"video=\"{videoFriendlyName}\""
                    : $"audio=\"{audioFriendlyName}\"";

            // dshow’da format zorlamak sürücüden sürücüye değişir; bu nedenle burada eklemiyoruz.
            // Gerekirse: -video_size {width}x{height} -framerate {fps} -pixel_format yuyv422 vb.
            return $"-f dshow -i {input}";
        }
    }
}
