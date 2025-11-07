// File: UniCast.App/Services/StreamController.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UniCast.Core.Models;
using UniCast.Core.Settings;
using UniCast.Core.Streaming;
using UniCast.Encoder;
using UniCast.Encoder.Extensions;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace UniCast.App.Services
{
    /// <summary>
    /// Yayını başlatma, hedefleri hazırlama ve FFMPEG argümanlarını üretme işlerini yönetir.
    /// Bu sürüm CS1739, CS8601 ve ResolveUrl eksikliğini giderir.
    /// </summary>
    public class StreamController
    {
        public string LastAdvisory { get; private set; } = string.Empty;

        /// <summary>
        /// UI'dan gelen ayarlar ve hedeflerle tek encode + çoklu RTMP çıkış (overlay ile) başlatır.
        /// </summary>
        public async Task<bool> StartStreamingAsync(SettingsData settings, IEnumerable<TargetItem> targets, Profile profile)
        {
            // Hedefleri Core modeline dönüştür
            var coreTargets = (targets ?? Enumerable.Empty<TargetItem>())
                .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Url))
                .Select(t => new StreamTarget
                {
                    Name = t.Name ?? string.Empty,
                    Url = t.Url ?? string.Empty,
                    Key = t.Key ?? string.Empty,
                    Enabled = true
                })
                .ToList();

            if (coreTargets.Count == 0)
            {
                LastAdvisory = "Aktif hedef bulunamadı.";
                return false;
            }

            // Overlay konumlandırma – mevcut mantığı koruyarak güvenli varsayımlar
            int overlayX = 20;
            int overlayY = Math.Max(0, (profile?.Height ?? 720) - 320);
            int overlayFps = Math.Max(1, settings?.Fps ?? 30);

            // *** KRİTİK: FfmpegArgsBuilder doğru imza ile çağrılıyor ***
            var build = FfmpegArgsBuilder.BuildSingleEncodeMultiRtmpWithOverlay(
                settings,
                coreTargets,
                overlayX: overlayX,
                overlayY: overlayY,
                overlayFps: overlayFps
            );

            // CS8601 fix: Advisory null ise boş string ata
            LastAdvisory = build.Advisory ?? string.Empty;

            // FFmpeg'i başlat (projende zaten varsa kendi runner'ını kullan)
            return await StartFfmpegAsync(build.Args);
        }

        /// <summary>
        /// Örnek FFmpeg başlatma. Projende süreç yönetimini farklı yapıyorsan bunu kaldır.
        /// </summary>
        private Task<bool> StartFfmpegAsync(string args)
        {
            // ffmpeg.exe konumu: projende ayarlardan geliyorsa orayı kullan.
            // Burada PATH'te bulunduğu varsayılıyor.
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = Directory.GetCurrentDirectory()
            };

            try
            {
                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.WriteLine(e.Data); };
                proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.WriteLine(e.Data); };

                bool started = proc.Start();
                if (started)
                {
                    proc.BeginErrorReadLine();
                    proc.BeginOutputReadLine();
                }
                return Task.FromResult(started);
            }
            catch (Exception ex)
            {
                LastAdvisory = $"FFmpeg başlatılamadı: {ex.Message}";
                return Task.FromResult(false);
            }
        }
    }

    // ---- NOTLAR ----
    // SettingsData, TargetItem, Profile tipleri projende zaten mevcut olmalı.
    // - SettingsData: en azından Fps bilgisini içeriyor olmalı (int Fps).
    // - TargetItem: Name, Url, Key, Enabled alanlarını içeriyor olmalı.
    // - Profile: en azından Height bilgisini içeriyor olmalı (int Height).
}
