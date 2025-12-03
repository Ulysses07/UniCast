using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using Serilog;

namespace UniCast.App.Services
{
    /// <summary>
    /// DÜZELTME v19: Kapsamlı konfigürasyon doğrulama
    /// Uygulama başlangıcında ve ayar değişikliklerinde çalışır
    /// </summary>
    public static class ConfigurationValidator
    {
        #region Validation Rules

        private static class Rules
        {
            // Video
            public const int MinVideoBitrate = 500;
            public const int MaxVideoBitrate = 50000;
            public const int MinFps = 15;
            public const int MaxFps = 60;
            public const int MinWidth = 640;
            public const int MaxWidth = 3840;
            public const int MinHeight = 360;
            public const int MaxHeight = 2160;

            // Audio
            public const int MinAudioBitrate = 32;
            public const int MaxAudioBitrate = 320;
            public const int MinAudioDelay = -5000;
            public const int MaxAudioDelay = 5000;

            // Stream
            public const int MinStreamKeyLength = 10;
            public const int MaxStreamKeyLength = 200;
        }

        #endregion

        #region Main Validation

        /// <summary>
        /// Tüm ayarları doğrula
        /// </summary>
        public static ValidationReport ValidateAll(SettingsData settings)
        {
            var report = new ValidationReport();

            // Video ayarları
            ValidateVideoSettings(settings, report);

            // Audio ayarları
            ValidateAudioSettings(settings, report);

            // Platform ayarları
            ValidatePlatformSettings(settings, report);

            // Kayıt ayarları
            ValidateRecordingSettings(settings, report);

            // Sistem gereksinimleri
            ValidateSystemRequirements(report);

            report.IsValid = !report.Errors.Any();
            return report;
        }

        /// <summary>
        /// Hızlı doğrulama (sadece kritik ayarlar)
        /// </summary>
        public static bool QuickValidate(SettingsData settings, out string? error)
        {
            error = null;

            // Video bitrate
            if (settings.VideoKbps < Rules.MinVideoBitrate || settings.VideoKbps > Rules.MaxVideoBitrate)
            {
                error = $"Video bitrate {Rules.MinVideoBitrate}-{Rules.MaxVideoBitrate} kbps arasında olmalı";
                return false;
            }

            // FPS
            if (settings.Fps < Rules.MinFps || settings.Fps > Rules.MaxFps)
            {
                error = $"FPS {Rules.MinFps}-{Rules.MaxFps} arasında olmalı";
                return false;
            }

            // Çözünürlük
            if (settings.Width < Rules.MinWidth || settings.Width > Rules.MaxWidth ||
                settings.Height < Rules.MinHeight || settings.Height > Rules.MaxHeight)
            {
                error = "Geçersiz çözünürlük";
                return false;
            }

            return true;
        }

        #endregion

        #region Video Validation

        private static void ValidateVideoSettings(SettingsData settings, ValidationReport report)
        {
            // Bitrate
            if (settings.VideoKbps < Rules.MinVideoBitrate)
            {
                report.AddError("VideoKbps", $"Video bitrate çok düşük (min: {Rules.MinVideoBitrate} kbps)");
            }
            else if (settings.VideoKbps > Rules.MaxVideoBitrate)
            {
                report.AddError("VideoKbps", $"Video bitrate çok yüksek (max: {Rules.MaxVideoBitrate} kbps)");
            }
            else if (settings.VideoKbps < 2500)
            {
                report.AddWarning("VideoKbps", "Düşük bitrate kalite sorunlarına yol açabilir");
            }

            // FPS
            if (settings.Fps < Rules.MinFps || settings.Fps > Rules.MaxFps)
            {
                report.AddError("Fps", $"FPS {Rules.MinFps}-{Rules.MaxFps} arasında olmalı");
            }
            else if (settings.Fps > 30 && settings.VideoKbps < 4500)
            {
                report.AddWarning("Fps", "Yüksek FPS için daha yüksek bitrate önerilir");
            }

            // Çözünürlük
            if (settings.Width < Rules.MinWidth || settings.Width > Rules.MaxWidth)
            {
                report.AddError("Width", $"Genişlik {Rules.MinWidth}-{Rules.MaxWidth} arasında olmalı");
            }

            if (settings.Height < Rules.MinHeight || settings.Height > Rules.MaxHeight)
            {
                report.AddError("Height", $"Yükseklik {Rules.MinHeight}-{Rules.MaxHeight} arasında olmalı");
            }

            // Aspect ratio kontrolü
            var aspectRatio = (double)settings.Width / settings.Height;
            if (aspectRatio < 1.0 || aspectRatio > 2.5)
            {
                report.AddWarning("Resolution", "Olağandışı aspect ratio, bazı platformlarda sorun olabilir");
            }

            // Encoder
            var validEncoders = new[] { "auto", "libx264", "h264_nvenc", "h264_amf", "h264_qsv" };
            if (!string.IsNullOrEmpty(settings.Encoder) && !validEncoders.Contains(settings.Encoder.ToLower()))
            {
                report.AddWarning("Encoder", $"Bilinmeyen encoder: {settings.Encoder}");
            }
        }

        #endregion

        #region Audio Validation

        private static void ValidateAudioSettings(SettingsData settings, ValidationReport report)
        {
            // Bitrate
            if (settings.AudioKbps < Rules.MinAudioBitrate || settings.AudioKbps > Rules.MaxAudioBitrate)
            {
                report.AddError("AudioKbps", $"Audio bitrate {Rules.MinAudioBitrate}-{Rules.MaxAudioBitrate} kbps arasında olmalı");
            }
            else if (settings.AudioKbps < 96)
            {
                report.AddWarning("AudioKbps", "Düşük audio bitrate kalite kaybına yol açabilir");
            }

            // Delay
            if (settings.AudioDelayMs < Rules.MinAudioDelay || settings.AudioDelayMs > Rules.MaxAudioDelay)
            {
                report.AddError("AudioDelayMs", $"Audio delay {Rules.MinAudioDelay}-{Rules.MaxAudioDelay} ms arasında olmalı");
            }
        }

        #endregion

        #region Platform Validation

        private static void ValidatePlatformSettings(SettingsData settings, ValidationReport report)
        {
            // YouTube
            if (!string.IsNullOrEmpty(settings.YouTubeStreamKey))
            {
                if (settings.YouTubeStreamKey.Length < Rules.MinStreamKeyLength)
                {
                    report.AddWarning("YouTube", "YouTube stream key çok kısa görünüyor");
                }
            }

            // Twitch
            if (!string.IsNullOrEmpty(settings.TwitchStreamKey))
            {
                if (!settings.TwitchStreamKey.StartsWith("live_"))
                {
                    report.AddWarning("Twitch", "Twitch stream key formatı doğru olmayabilir");
                }
            }

            // Instagram
            if (!string.IsNullOrEmpty(settings.InstagramSessionId))
            {
                // Session ID formatı kontrolü
                if (settings.InstagramSessionId.Length < 20)
                {
                    report.AddWarning("Instagram", "Instagram session ID kısa görünüyor");
                }
            }

            // Facebook
            if (!string.IsNullOrEmpty(settings.FacebookAccessToken))
            {
                if (settings.FacebookAccessToken.Length < 50)
                {
                    report.AddWarning("Facebook", "Facebook access token kısa görünüyor");
                }
            }
        }

        #endregion

        #region Recording Validation

        private static void ValidateRecordingSettings(SettingsData settings, ValidationReport report)
        {
            if (!settings.EnableLocalRecord)
                return;

            if (string.IsNullOrWhiteSpace(settings.RecordFolder))
            {
                report.AddError("RecordFolder", "Yerel kayıt aktif ama klasör belirtilmemiş");
                return;
            }

            // Klasör var mı?
            if (!Directory.Exists(settings.RecordFolder))
            {
                try
                {
                    Directory.CreateDirectory(settings.RecordFolder);
                    report.AddInfo("RecordFolder", "Kayıt klasörü oluşturuldu");
                }
                catch (Exception ex)
                {
                    report.AddError("RecordFolder", $"Kayıt klasörü oluşturulamadı: {ex.Message}");
                    return;
                }
            }

            // Yazma izni var mı?
            try
            {
                var testFile = Path.Combine(settings.RecordFolder, ".unicast_test");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
            }
            catch
            {
                report.AddError("RecordFolder", "Kayıt klasörüne yazma izni yok");
            }

            // Disk alanı kontrolü
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(settings.RecordFolder) ?? "C:");
                var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);

                if (freeGb < 1)
                {
                    report.AddError("RecordFolder", "Disk alanı kritik seviyede düşük (<1 GB)");
                }
                else if (freeGb < 10)
                {
                    report.AddWarning("RecordFolder", $"Disk alanı düşük ({freeGb:F1} GB kaldı)");
                }
            }
            catch
            {
                // Disk bilgisi alınamadı
            }
        }

        #endregion

        #region System Validation

        private static void ValidateSystemRequirements(ValidationReport report)
        {
            // FFmpeg kontrolü
            var ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                report.AddError("FFmpeg", "FFmpeg bulunamadı. Lütfen FFmpeg'i yükleyin.");
            }

            // RAM kontrolü
            try
            {
                var ramInfo = new Microsoft.VisualBasic.Devices.ComputerInfo();
                var totalRamGb = ramInfo.TotalPhysicalMemory / (1024.0 * 1024 * 1024);
                var availableRamGb = ramInfo.AvailablePhysicalMemory / (1024.0 * 1024 * 1024);

                if (availableRamGb < 1)
                {
                    report.AddWarning("RAM", $"Kullanılabilir RAM düşük ({availableRamGb:F1} GB)");
                }
            }
            catch
            {
                // RAM bilgisi alınamadı
            }

            // Ağ bağlantısı kontrolü
            if (!IsNetworkAvailable())
            {
                report.AddWarning("Network", "Ağ bağlantısı algılanamadı");
            }
        }

        private static string? FindFFmpeg()
        {
            var possiblePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe"
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                    return path;
            }

            // PATH'te ara
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(';'))
            {
                var path = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        private static bool IsNetworkAvailable()
        {
            try
            {
                return NetworkInterface.GetIsNetworkAvailable();
            }
            catch
            {
                return true; // Varsayılan olarak var kabul et
            }
        }

        #endregion
    }

    #region Validation Report

    /// <summary>
    /// Doğrulama raporu
    /// </summary>
    public class ValidationReport
    {
        public bool IsValid { get; set; } = true;
        public List<ValidationMessage> Errors { get; } = new();
        public List<ValidationMessage> Warnings { get; } = new();
        public List<ValidationMessage> Info { get; } = new();

        public void AddError(string field, string message)
        {
            Errors.Add(new ValidationMessage(field, message, ValidationSeverity.Error));
            Log.Error("[Validation] {Field}: {Message}", field, message);
        }

        public void AddWarning(string field, string message)
        {
            Warnings.Add(new ValidationMessage(field, message, ValidationSeverity.Warning));
            Log.Warning("[Validation] {Field}: {Message}", field, message);
        }

        public void AddInfo(string field, string message)
        {
            Info.Add(new ValidationMessage(field, message, ValidationSeverity.Info));
            Log.Information("[Validation] {Field}: {Message}", field, message);
        }

        public string GetSummary()
        {
            var parts = new List<string>();

            if (Errors.Any())
                parts.Add($"{Errors.Count} hata");
            if (Warnings.Any())
                parts.Add($"{Warnings.Count} uyarı");
            if (Info.Any())
                parts.Add($"{Info.Count} bilgi");

            return parts.Any() ? string.Join(", ", parts) : "Sorun yok";
        }
    }

    public record ValidationMessage(string Field, string Message, ValidationSeverity Severity);

    public enum ValidationSeverity
    {
        Info,
        Warning,
        Error
    }

    #endregion
}
