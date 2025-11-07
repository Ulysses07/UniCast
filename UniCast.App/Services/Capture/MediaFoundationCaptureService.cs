using System.Linq;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace UniCast.App.Services.Capture
{
    public sealed class MediaFoundationCaptureService : IDeviceService
    {
        public async Task<IReadOnlyList<string>> GetVideoFriendlyNamesAsync()
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            return devices.Select(d => d.Name).ToList();
        }

        public async Task<IReadOnlyList<string>> GetAudioFriendlyNamesAsync()
        {
            var selector = MediaDevice.GetAudioCaptureSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);
            return devices.Select(d => d.Name).ToList();
        }

        public string BuildFfmpegInputArgs(string? video, string? audio, int? width = null, int? height = null, int? fps = null)
        {
            if (string.IsNullOrWhiteSpace(video) && string.IsNullOrWhiteSpace(audio))
                return string.Empty;

            var input = (video, audio) switch
            {
                ({ } v, { } a) => $"video=\"{v}\":audio=\"{a}\"",
                ({ } v, null) => $"video=\"{v}\"",
                (null, { } a) => $"audio=\"{a}\"",
                _ => string.Empty
            };

            // Not: dshow'da format zorlamak sürücüye bağlı. Çoğu durumda scale/fps filtreleri encoder tarafında daha stabil.
            return $"-f dshow -i {input}";
        }
    }
}
