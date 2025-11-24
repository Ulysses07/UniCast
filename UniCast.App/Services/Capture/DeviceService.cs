using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UniCast.Core.Models;
using UniCast.Capture; // REFERANS: Unicast.Capture Projesi

namespace UniCast.App.Services.Capture
{
    public class DeviceService : IDeviceService
    {
        private readonly MediaFoundationCaptureService _captureService;

        public DeviceService()
        {
            // Kendi yazdığımız modern servisi başlatıyoruz
            _captureService = new MediaFoundationCaptureService();
        }

        public async Task<List<CaptureDevice>> GetVideoDevicesAsync()
        {
            return await _captureService.GetVideoDevicesAsync();
        }

        public async Task<List<CaptureDevice>> GetAudioDevicesAsync()
        {
            return await _captureService.GetAudioDevicesAsync();
        }

        public async Task<string?> GetDeviceNameByIdAsync(string id)
        {
            // ID verince ismini bulur (FFmpeg için gerekli)
            var videos = await GetVideoDevicesAsync();
            var v = videos.FirstOrDefault(x => x.Id == id);
            if (v != null) return v.Name;

            var audios = await GetAudioDevicesAsync();
            var a = audios.FirstOrDefault(x => x.Id == id);
            if (a != null) return a.Name;

            return null;
        }
    }
}