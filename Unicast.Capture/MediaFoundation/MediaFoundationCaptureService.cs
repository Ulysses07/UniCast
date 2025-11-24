using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration; // Modern Windows API
using UniCast.Core.Models;         // Core Model (CaptureDevice)

namespace UniCast.Capture
{
    public sealed class MediaFoundationCaptureService
    {
        // Video Cihazlarını Getir (Kamera, Capture Card vb.)
        public async Task<List<CaptureDevice>> GetVideoDevicesAsync()
        {
            // Sistemdeki tüm video giriş cihazlarını bul
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

            return devices.Select(d => new CaptureDevice
            {
                Name = d.Name,
                Id = d.Id // FFmpeg için gereken benzersiz kimlik (SymLink)
            }).ToList();
        }

        // Ses Cihazlarını Getir (Mikrofonlar)
        public async Task<List<CaptureDevice>> GetAudioDevicesAsync()
        {
            // Ses yakalama cihazlarını bul
            var selector = Windows.Media.Devices.MediaDevice.GetAudioCaptureSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);

            return devices.Select(d => new CaptureDevice
            {
                Name = d.Name,
                Id = d.Id
            }).ToList();
        }
    }
}