using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration; // Microsoft.Windows.SDK.Contracts
using UniCast.Capture.MediaFoundation;

namespace UniCast.Capture
{
    public interface IDeviceService
    {
        Task<IReadOnlyList<MediaFoundationDevice>> GetVideoDevicesAsync();
        Task<IReadOnlyList<MediaFoundationDevice>> GetAudioDevicesAsync();
        /// <summary>
        /// dshow tabanlı FFmpeg giriş argümanını üretir:
        /// -f dshow -i video="FriendlyName":audio="FriendlyName"
        /// </summary>
        string BuildFfmpegInputArgs(string? videoFriendlyName, string? audioFriendlyName, int? width = null, int? height = null, int? fps = null);
    }

    public sealed class MediaFoundationCaptureService : IDeviceService
    {
        public async Task<IReadOnlyList<MediaFoundationDevice>> GetVideoDevicesAsync()
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            return devices.Select(d => new MediaFoundationDevice
            {
                FriendlyName = d.Name,
                SymbolicLink = d.Id,
                Manufacturer = d.Kind.ToString()
            }).ToList();
        }

        public async Task<IReadOnlyList<MediaFoundationDevice>> GetAudioDevicesAsync()
        {
            // Mikrofonlar için:
            var selector = MediaDevice.GetAudioCaptureSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);
            return devices.Select(d => new MediaFoundationDevice
            {
                FriendlyName = d.Name,
                SymbolicLink = d.Id,
                Manufacturer = d.Kind.ToString()
            }).ToList();
        }

        public string BuildFfmpegInputArgs(string? videoFriendlyName, string? audioFriendlyName, int? width = null, int? height = null, int? fps = null)
        {
            // dshow argümanı friendly name ile eşleşir.
            // En stabil kullanım: sadece isimle bağlanmak.
            // Format zorlamaları (width/height/fps) driver’a göre değişir; çoğunlukla encoder tarafında ölçeklemek daha kararlı.
            var parts = new List<string> { "-f dshow" };

            string va = string.Empty;
            if (!string.IsNullOrWhiteSpace(videoFriendlyName) && !string.IsNullOrWhiteSpace(audioFriendlyName))
            {
                va = $"video=\"{videoFriendlyName}\":audio=\"{audioFriendlyName}\"";
            }
            else if (!string.IsNullOrWhiteSpace(videoFriendlyName))
            {
                va = $"video=\"{videoFriendlyName}\"";
            }
            else if (!string.IsNullOrWhiteSpace(audioFriendlyName))
            {
                va = $"audio=\"{audioFriendlyName}\"";
            }
            else
            {
                // Hiç cihaz yoksa boş döner; çağıran taraf fallback (testsrc/anullsrc) kullanmalı.
                return string.Empty;
            }

            // format şart koşmak istersen dshow pin negotiation kullanılabilir ama her sürücü farklı davranır.
            // Bu yüzden çözünürlük/FPS’i encoder aşamasında scale/fps filter ile ayarlamak daha güvenli.
            parts.Add("-i");
            parts.Add(va);
            return string.Join(' ', parts);
        }
    }

    // WinRT MediaDevice helper
    internal static class MediaDevice
    {
        public static string GetAudioCaptureSelector()
            => Windows.Media.Devices.MediaDevice.GetAudioCaptureSelector();
    }
}
