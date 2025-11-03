using System.Collections.Generic;
using System.Linq;
using DirectShowLib;

namespace UniCast.App.Services
{
    /// <summary>
    /// DirectShow üzerinden cihazları listeler (DroidCam, Camo, OBS Virtual Camera vs.)
    /// </summary>
    public sealed class DeviceService : IDeviceService
    {
        public (IEnumerable<string> video, IEnumerable<string> audio) ListDevices()
        {
            // Video girişleri
            var v = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)
                            .Select(d => d.Name)
                            .Distinct()
                            .ToList();

            // Audio girişleri (mikrofon)
            var a = DsDevice.GetDevicesOfCat(FilterCategory.AudioInputDevice)
                            .Select(d => d.Name)
                            .Distinct()
                            .ToList();

            return (v, a);
        }
    }
}
