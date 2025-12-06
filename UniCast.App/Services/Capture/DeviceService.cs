using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UniCast.Core.Models;
using UniCast.Capture.MediaFoundation;

namespace UniCast.App.Services.Capture
{
    public class DeviceService : IDeviceService
    {
        private readonly MediaFoundationCaptureService _captureService;
        private static List<CaptureDevice>? _cachedVideoDevices;
        private static List<CaptureDevice>? _cachedAudioDevices;
        private static DateTime _cacheTime = DateTime.MinValue;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

        public DeviceService()
        {
            _captureService = new MediaFoundationCaptureService();
        }

        public async Task<List<CaptureDevice>> GetVideoDevicesAsync()
        {
            // Önce FFmpeg listesini dene (daha güvenilir)
            var ffmpegDevices = await GetFFmpegDevicesAsync();
            if (ffmpegDevices.video.Count > 0)
            {
                return ffmpegDevices.video;
            }

            // Fallback: WinRT API
            return await _captureService.GetVideoDevicesAsync();
        }

        public async Task<List<CaptureDevice>> GetAudioDevicesAsync()
        {
            // Önce FFmpeg listesini dene
            var ffmpegDevices = await GetFFmpegDevicesAsync();
            if (ffmpegDevices.audio.Count > 0)
            {
                return ffmpegDevices.audio;
            }

            // Fallback: WinRT API
            return await _captureService.GetAudioDevicesAsync();
        }

        public async Task<string?> GetDeviceNameByIdAsync(string id)
        {
            // Önce FFmpeg cihazlarında ara (alternative name ile eşleştir)
            var ffmpegDevices = await GetFFmpegDevicesAsync();

            // Video cihazlarında ara
            foreach (var device in ffmpegDevices.video)
            {
                if (device.Id.Contains(id, StringComparison.OrdinalIgnoreCase) ||
                    id.Contains(device.Id, StringComparison.OrdinalIgnoreCase) ||
                    MatchesDeviceId(device.Id, id))
                {
                    return device.Name;
                }
            }

            // Audio cihazlarında ara
            foreach (var device in ffmpegDevices.audio)
            {
                if (device.Id.Contains(id, StringComparison.OrdinalIgnoreCase) ||
                    id.Contains(device.Id, StringComparison.OrdinalIgnoreCase) ||
                    MatchesDeviceId(device.Id, id))
                {
                    return device.Name;
                }
            }

            // WinRT API'den dene
            var videos = await _captureService.GetVideoDevicesAsync();
            var v = videos.FirstOrDefault(x => x.Id == id);
            if (v != null) return v.Name;

            var audios = await _captureService.GetAudioDevicesAsync();
            var a = audios.FirstOrDefault(x => x.Id == id);
            if (a != null) return a.Name;

            return null;
        }

        private bool MatchesDeviceId(string ffmpegId, string winrtId)
        {
            // GUID veya cihaz yolundaki ortak kısımları karşılaştır
            // Örnek: "root#media#0001" her ikisinde de olabilir
            var ffmpegLower = ffmpegId.ToLowerInvariant();
            var winrtLower = winrtId.ToLowerInvariant();

            // GUID pattern: {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}
            var guidPattern = @"\{?[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}\}?";
            var ffmpegGuids = Regex.Matches(ffmpegLower, guidPattern);
            var winrtGuids = Regex.Matches(winrtLower, guidPattern);

            foreach (Match fg in ffmpegGuids)
            {
                foreach (Match wg in winrtGuids)
                {
                    if (fg.Value.Trim('{', '}') == wg.Value.Trim('{', '}'))
                        return true;
                }
            }

            // "root#media#0001" gibi ortak path segmentleri
            if (ffmpegLower.Contains("root#media") && winrtLower.Contains("root#media"))
                return true;
            if (ffmpegLower.Contains("root#camera") && winrtLower.Contains("root#camera"))
                return true;

            return false;
        }

        private async Task<(List<CaptureDevice> video, List<CaptureDevice> audio)> GetFFmpegDevicesAsync()
        {
            // Cache kontrolü
            if (_cachedVideoDevices != null && _cachedAudioDevices != null &&
                DateTime.Now - _cacheTime < CacheDuration)
            {
                return (_cachedVideoDevices, _cachedAudioDevices);
            }

            var videoDevices = new List<CaptureDevice>();
            var audioDevices = new List<CaptureDevice>();

            try
            {
                var ffmpegPath = FindFFmpegPath();
                System.Diagnostics.Debug.WriteLine($"[DeviceService] FFmpeg path: {ffmpegPath}");

                if (string.IsNullOrEmpty(ffmpegPath) || !System.IO.File.Exists(ffmpegPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[DeviceService] FFmpeg bulunamadı: {ffmpegPath}");
                    return (videoDevices, audioDevices);
                }

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-list_devices true -f dshow -i dummy",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    System.Diagnostics.Debug.WriteLine("[DeviceService] FFmpeg process başlatılamadı");
                    return (videoDevices, audioDevices);
                }

                // Timeout ile bekle
                var outputTask = process.StandardError.ReadToEndAsync();
                var completed = await Task.WhenAny(outputTask, Task.Delay(5000));

                if (completed != outputTask)
                {
                    System.Diagnostics.Debug.WriteLine("[DeviceService] FFmpeg timeout");
                    try { process.Kill(); } catch { }
                    return (videoDevices, audioDevices);
                }

                var output = await outputTask;
                System.Diagnostics.Debug.WriteLine($"[DeviceService] FFmpeg output length: {output?.Length ?? 0}");

                if (string.IsNullOrEmpty(output))
                {
                    return (videoDevices, audioDevices);
                }

                // Parse FFmpeg output
                var lines = output.Split('\n');
                string? currentDevice = null;
                string? currentType = null;

                foreach (var line in lines)
                {
                    // Device name line: [dshow @ xxx] "Name" (type)
                    var deviceMatch = Regex.Match(line, @"\[dshow @.*\]\s+""(.+)""\s+\((video|audio|none)\)");
                    if (deviceMatch.Success)
                    {
                        currentDevice = deviceMatch.Groups[1].Value;
                        currentType = deviceMatch.Groups[2].Value;
                        System.Diagnostics.Debug.WriteLine($"[DeviceService] Cihaz bulundu: {currentDevice} ({currentType})");
                        continue;
                    }

                    // Alternative name line
                    var altMatch = Regex.Match(line, @"Alternative name ""(.+)""");
                    if (altMatch.Success && currentDevice != null && currentType != null)
                    {
                        var altName = altMatch.Groups[1].Value;
                        var device = new CaptureDevice
                        {
                            Name = currentDevice,
                            Id = altName
                        };

                        if (currentType == "video")
                            videoDevices.Add(device);
                        else if (currentType == "audio")
                            audioDevices.Add(device);

                        currentDevice = null;
                        currentType = null;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[DeviceService] Toplam: {videoDevices.Count} video, {audioDevices.Count} audio cihaz");

                // Cache güncelle
                _cachedVideoDevices = videoDevices;
                _cachedAudioDevices = audioDevices;
                _cacheTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DeviceService] Hata: {ex.Message}");
            }

            return (videoDevices, audioDevices);
        }

        private string? FindFFmpegPath()
        {
            // Uygulama dizininde ara
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var possiblePaths = new[]
            {
                System.IO.Path.Combine(appDir, "ffmpeg.exe"),
                System.IO.Path.Combine(appDir, "ffmpeg", "ffmpeg.exe"),
                System.IO.Path.Combine(appDir, "tools", "ffmpeg.exe"),
            };

            foreach (var path in possiblePaths)
            {
                System.Diagnostics.Debug.WriteLine($"[DeviceService] FFmpeg kontrol: {path} - Exists: {System.IO.File.Exists(path)}");
                if (System.IO.File.Exists(path))
                    return path;
            }

            // Son çare - uygulama dizininde olduğunu varsay
            var defaultPath = System.IO.Path.Combine(appDir, "ffmpeg.exe");
            System.Diagnostics.Debug.WriteLine($"[DeviceService] Varsayılan FFmpeg path: {defaultPath}");
            return defaultPath;
        }
    }
}