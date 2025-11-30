using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Serilog;

namespace UniCast.App.Services
{
    /// <summary>
    /// Uygulama ayarlarını yöneten merkezi store.
    /// Thread-safe, auto-save destekli.
    /// </summary>
    public static class SettingsStore
    {
        private static readonly string SettingsPath;
        private static readonly object _lock = new();
        private static SettingsData? _data;
        private static System.Threading.Timer? _autoSaveTimer;
        private static bool _isDirty;

        static SettingsStore()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var settingsDir = Path.Combine(appData, "UniCast");

            try
            {
                Directory.CreateDirectory(settingsDir);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SettingsStore] Settings dizini oluşturulamadı");
            }

            SettingsPath = Path.Combine(settingsDir, "settings.json");
        }

        /// <summary>
        /// Mevcut ayarlar.
        /// </summary>
        public static SettingsData Data
        {
            get
            {
                lock (_lock)
                {
                    if (_data == null)
                    {
                        _data = Load();
                        StartAutoSave();
                    }
                    return _data;
                }
            }
        }

        /// <summary>
        /// Ayarları günceller.
        /// </summary>
        public static void Update(Action<SettingsData> updateAction)
        {
            lock (_lock)
            {
                var data = Data;
                updateAction(data);
                _isDirty = true;
            }
        }

        /// <summary>
        /// Ayarları hemen kaydeder.
        /// </summary>
        public static void Save()
        {
            lock (_lock)
            {
                if (_data == null)
                    return;

                try
                {
                    _data.Normalize();

                    var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    // Atomic write
                    var tempPath = SettingsPath + ".tmp";
                    File.WriteAllText(tempPath, json);

                    if (File.Exists(SettingsPath))
                        File.Delete(SettingsPath);

                    File.Move(tempPath, SettingsPath);

                    _isDirty = false;
                    Log.Debug("[SettingsStore] Ayarlar kaydedildi");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[SettingsStore] Kaydetme hatası");
                }
            }
        }

        /// <summary>
        /// Ayarları yeniden yükler.
        /// </summary>
        public static void Reload()
        {
            lock (_lock)
            {
                _data = Load();
                _isDirty = false;
            }
        }

        /// <summary>
        /// Varsayılan ayarlara sıfırlar.
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _data = new SettingsData();
                _data.Normalize();
                Save();
            }
        }

        /// <summary>
        /// Kaynakları temizler.
        /// </summary>
        public static void Cleanup()
        {
            lock (_lock)
            {
                // Auto-save timer'ı durdur
                _autoSaveTimer?.Dispose();
                _autoSaveTimer = null;

                // Dirty ise kaydet
                if (_isDirty)
                {
                    Save();
                }
            }
        }

        private static SettingsData Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var data = JsonSerializer.Deserialize<SettingsData>(json);

                    if (data != null)
                    {
                        data.Normalize();
                        Log.Debug("[SettingsStore] Ayarlar yüklendi");
                        return data;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SettingsStore] Yükleme hatası");
            }

            Log.Information("[SettingsStore] Varsayılan ayarlar kullanılıyor");
            var defaults = new SettingsData();
            defaults.Normalize();
            return defaults;
        }

        private static void StartAutoSave()
        {
            _autoSaveTimer?.Dispose();
            _autoSaveTimer = new System.Threading.Timer(_ =>
            {
                if (_isDirty)
                {
                    Save();
                }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// Uygulama ayarları veri modeli.
    /// </summary>
    public sealed class SettingsData
    {
        // Stream Ayarları
        public int VideoKbps { get; set; } = 2500;
        public int AudioKbps { get; set; } = 128;
        public int Fps { get; set; } = 30;
        public string VideoResolution { get; set; } = "1920x1080";
        public string VideoEncoder { get; set; } = "libx264";
        public string VideoPreset { get; set; } = "veryfast";
        public string AudioEncoder { get; set; } = "aac";
        public int AudioSampleRate { get; set; } = 44100;

        // Platform Bağlantıları
        public string YouTubeVideoId { get; set; } = "";
        public string YouTubeStreamKey { get; set; } = "";
        public string TwitchStreamKey { get; set; } = "";
        public string TikTokUsername { get; set; } = "";
        public string InstagramUsername { get; set; } = "";
        public string FacebookPageId { get; set; } = "";
        public string FacebookStreamKey { get; set; } = "";
        public string CustomRtmpUrl { get; set; } = "";

        // Overlay Ayarları
        public bool OverlayEnabled { get; set; } = true;
        public double OverlayOpacity { get; set; } = 0.9;
        public string OverlayPosition { get; set; } = "BottomRight";
        public int OverlayWidth { get; set; } = 400;
        public int OverlayHeight { get; set; } = 300;
        public string OverlayTheme { get; set; } = "Dark";
        public bool OverlayShowChat { get; set; } = true;
        public int OverlayChatMessageLimit { get; set; } = 50;

        // Chat Ayarları
        public bool ChatEnabled { get; set; } = true;
        public bool ChatShowTimestamps { get; set; } = true;
        public bool ChatShowPlatformBadges { get; set; } = true;
        public bool ChatFilterProfanity { get; set; } = false;
        public string[] ChatBlockedWords { get; set; } = Array.Empty<string>();
        public string[] ChatBlockedUsers { get; set; } = Array.Empty<string>();

        // Genel Ayarlar
        public bool StartMinimized { get; set; } = false;
        public bool MinimizeToTray { get; set; } = true;
        public bool StartWithWindows { get; set; } = false;
        public bool CheckForUpdates { get; set; } = true;
        public string Language { get; set; } = "tr-TR";
        public string Theme { get; set; } = "Dark";

        // Kayıt Ayarları
        public bool RecordingEnabled { get; set; } = false;
        public string RecordingPath { get; set; } = "";
        public string RecordingFormat { get; set; } = "mp4";
        public int RecordingQuality { get; set; } = 80;

        // API Keys (şifrelenmiş saklanmalı)
        public string YouTubeApiKey { get; set; } = "";
        public string TwitchClientId { get; set; } = "";
        public string FacebookAccessToken { get; set; } = "";

        /// <summary>
        /// Değerleri normalize eder ve sınırlar içinde tutar.
        /// </summary>
        public void Normalize()
        {
            // Video
            VideoKbps = Math.Clamp(VideoKbps, 500, 50000);
            AudioKbps = Math.Clamp(AudioKbps, 64, 320);
            Fps = Math.Clamp(Fps, 15, 120);

            // Overlay
            OverlayOpacity = Math.Clamp(OverlayOpacity, 0.1, 1.0);
            OverlayWidth = Math.Clamp(OverlayWidth, 200, 1920);
            OverlayHeight = Math.Clamp(OverlayHeight, 150, 1080);
            OverlayChatMessageLimit = Math.Clamp(OverlayChatMessageLimit, 10, 500);

            // Recording
            RecordingQuality = Math.Clamp(RecordingQuality, 1, 100);

            // Varsayılan kayıt yolu
            if (string.IsNullOrEmpty(RecordingPath))
            {
                RecordingPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                    "UniCast Recordings");
            }

            // Null array'leri düzelt
            ChatBlockedWords ??= Array.Empty<string>();
            ChatBlockedUsers ??= Array.Empty<string>();

            // Boş string'leri düzelt
            VideoResolution ??= "1920x1080";
            VideoEncoder ??= "libx264";
            VideoPreset ??= "veryfast";
            AudioEncoder ??= "aac";
            OverlayPosition ??= "BottomRight";
            OverlayTheme ??= "Dark";
            Language ??= "tr-TR";
            Theme ??= "Dark";
            RecordingFormat ??= "mp4";
        }
    }
}