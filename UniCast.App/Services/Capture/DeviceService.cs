using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UniCast.Core.Models;
using UniCast.Capture; // Unicast.Capture kütüphanesi referansı

namespace UniCast.App.Services.Capture
{
    public class DeviceService : IDeviceService
    {
        private readonly MediaFoundationCaptureService _captureService;

        public DeviceService()
        {
            // Core kütüphanedeki servisi başlat
            _captureService = new MediaFoundationCaptureService();
        }

        public async Task<List<CaptureDevice>> GetVideoDevicesAsync()
        {
            // HATA DÜZELTME: Metot adı GetVideoDevicesAsync oldu ve await eklendi.
            return await _captureService.GetVideoDevicesAsync();
        }

        public async Task<List<CaptureDevice>> GetAudioDevicesAsync()
        {
            // HATA DÜZELTME: Metot adı GetAudioDevicesAsync oldu ve await eklendi.
            return await _captureService.GetAudioDevicesAsync();
        }

        public async Task<string?> GetDeviceNameByIdAsync(string id)
        {
            // ID'ye göre doğru cihaz ismini bulma mantığı
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