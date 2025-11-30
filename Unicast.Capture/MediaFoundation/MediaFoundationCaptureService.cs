using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration; // WinRT Modern API
using UniCast.Core.Models;

namespace UniCast.Capture.MediaFoundation
{
    public sealed class MediaFoundationCaptureService
    {
        public async Task<List<CaptureDevice>> GetVideoDevicesAsync()
        {
            // AForge veya MfInterop yerine modern Windows API
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

            return devices.Select(d => new CaptureDevice
            {
                Name = d.Name,
                Id = d.Id
            }).ToList();
        }

        public async Task<List<CaptureDevice>> GetAudioDevicesAsync()
        {
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