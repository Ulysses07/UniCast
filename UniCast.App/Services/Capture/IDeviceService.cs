using System.Collections.Generic;
using System.Threading.Tasks;
using UniCast.Core.Models;

namespace UniCast.App.Services.Capture
{
    public interface IDeviceService
    {
        // Artık CaptureDevice listesi dönüyor
        Task<List<CaptureDevice>> GetVideoDevicesAsync();
        Task<List<CaptureDevice>> GetAudioDevicesAsync();

        // FFmpeg için ID'den İsim bulan metot
        Task<string?> GetDeviceNameByIdAsync(string id);
    }
}