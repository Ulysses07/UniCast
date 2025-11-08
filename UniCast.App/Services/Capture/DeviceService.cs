using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AForge.Video.DirectShow;

namespace UniCast.App.Services.Capture
{
    /// <summary>
    /// Cihaz adlarını DirectShow üzerinden toplar.
    /// Böylece FFmpeg -f dshow ile birebir aynı "friendly name" gelir.
    /// </summary>
    public sealed class DeviceService : IDeviceService
    {
        public Task<IReadOnlyList<string>> GetVideoFriendlyNamesAsync()
        {
            var list = new List<string>();
            try
            {
                var coll = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                foreach (FilterInfo fi in coll)
                    if (!string.IsNullOrWhiteSpace(fi?.Name))
                        list.Add(fi.Name);
            }
            catch { /* ignore */ }

            // Tercih edilen sıraya göre küçük bir kalite: Camo/DroidCam üstlere
            list = list
                .OrderByDescending(n => n.Contains("Camo", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(n => n.Contains("DroidCam", StringComparison.OrdinalIgnoreCase))
                .ThenBy(n => n)
                .ToList();

            return Task.FromResult<IReadOnlyList<string>>(list);
        }

        public Task<IReadOnlyList<string>> GetAudioFriendlyNamesAsync()
        {
            var list = new List<string>();
            try
            {
                var coll = new FilterInfoCollection(FilterCategory.AudioInputDevice);
                foreach (FilterInfo fi in coll)
                    if (!string.IsNullOrWhiteSpace(fi?.Name))
                        list.Add(fi.Name);
            }
            catch { /* ignore */ }

            // Benzer öncelik sırası
            list = list
                .OrderByDescending(n => n.Contains("Camo", StringComparison.OrdinalIgnoreCase)
                                     || n.Contains("DroidCam", StringComparison.OrdinalIgnoreCase))
                .ThenBy(n => n)
                .ToList();

            return Task.FromResult<IReadOnlyList<string>>(list);
        }

        /// <summary>
        /// FFmpeg dshow girdisini üretir (tek veya çift giriş).
        /// Not: Uygulamada FfmpegArgsBuilder zaten iki ayrı -i kullanıyor;
        /// bu metodu kullansan da kullanmasan da adlar DirectShow'tan geldiği için eşleşir.
        /// </summary>
        public string BuildFfmpegInputArgs(string? videoFriendlyName, string? audioFriendlyName,
                                           int? width = null, int? height = null, int? fps = null)
        {
            bool hasVideo = !string.IsNullOrWhiteSpace(videoFriendlyName);
            bool hasAudio = !string.IsNullOrWhiteSpace(audioFriendlyName);

            if (!hasVideo && !hasAudio)
                return string.Empty;

            // DİKKAT: "video=" ve "audio=" adlar tırnak içinde olmalı, ':' kaçışı GEREKMİYOR
            // Ayrı girişler kullanıyorsak iki tane -f dshow -i yazacağız (uygulamadaki mantık).
            // İlla tek satır yapmak istersen: audio="...":video="..." biçimi de kullanılabilir.

            var parts = new List<string>();

            if (hasVideo)
            {
                var vopts = new List<string> { "-f", "dshow", "-thread_queue_size", "512" };
                if (width is int w && height is int h) { vopts.AddRange(new[] { "-video_size", $"{w}x{h}" }); }
                if (fps is int f) { vopts.AddRange(new[] { "-framerate", $"{f}" }); }
                vopts.AddRange(new[] { "-i", $"video=\"{videoFriendlyName}\"" });
                parts.Add(string.Join(' ', vopts));
            }

            if (hasAudio)
            {
                var aopts = new List<string> { "-f", "dshow", "-thread_queue_size", "512" };
                // Genelde sample rate/format dayatmayız; sürücüye bırakırız.
                aopts.AddRange(new[] { "-i", $"audio=\"{audioFriendlyName}\"" });
                parts.Add(string.Join(' ', aopts));
            }

            return string.Join(' ', parts);
        }
    }
}
