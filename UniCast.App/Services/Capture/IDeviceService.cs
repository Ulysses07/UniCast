using System.Collections.Generic;
using System.Threading.Tasks;
using UniCast.Core.Models; // CaptureDevice modelini tanıması için

namespace UniCast.App.Services.Capture
{
    public interface IDeviceService
    {
        // Eski metodlar yerine bunları kullanıyoruz:
        Task<List<CaptureDevice>> GetVideoDevicesAsync();
        Task<List<CaptureDevice>> GetAudioDevicesAsync();

        // FFmpeg için ID -> İsim çevirici
        Task<string?> GetDeviceNameByIdAsync(string id);
    }
}