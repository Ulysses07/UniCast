using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Serilog;
using UniCast.Core.Models;
using UniCast.App.Security;

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
        /// Mevcut ayarlar (Data ile aynı, alias).
        /// </summary>
        public static SettingsData Current => Data;

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

        /// <summary>
        /// Ayarları yükler.
        /// </summary>
        public static SettingsData Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var data = JsonSerializer.Deserialize<SettingsData>(json);

                    if (data != null)
                    {
                        // ESKİ AYARLARDAN MİGRASYON
                        // Eski JSON'da şifrelenmemiş key'ler varsa, bunları şifreli versiyonlara aktar
                        MigrateOldSettings(json, data);

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

        /// <summary>
        /// Eski şifrelenmemiş ayarları yeni şifreli formata migrate eder.
        /// </summary>
        private static void MigrateOldSettings(string json, SettingsData data)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                bool migrated = false;

                // YouTube Stream Key
                if (string.IsNullOrEmpty(data.EncryptedYouTubeStreamKey) &&
                    root.TryGetProperty("YouTubeStreamKey", out var ytStreamKey))
                {
                    var value = ytStreamKey.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        data.YouTubeStreamKey = value; // Bu setter şifreleyecek
                        migrated = true;
                        Log.Information("[SettingsStore] YouTube Stream Key migrate edildi");
                    }
                }

                // Twitch Stream Key
                if (string.IsNullOrEmpty(data.EncryptedTwitchStreamKey) &&
                    root.TryGetProperty("TwitchStreamKey", out var twitchStreamKey))
                {
                    var value = twitchStreamKey.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        data.TwitchStreamKey = value;
                        migrated = true;
                        Log.Information("[SettingsStore] Twitch Stream Key migrate edildi");
                    }
                }

                // Facebook Stream Key
                if (string.IsNullOrEmpty(data.EncryptedFacebookStreamKey) &&
                    root.TryGetProperty("FacebookStreamKey", out var fbStreamKey))
                {
                    var value = fbStreamKey.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        data.FacebookStreamKey = value;
                        migrated = true;
                        Log.Information("[SettingsStore] Facebook Stream Key migrate edildi");
                    }
                }

                // YouTube API Key
                if (string.IsNullOrEmpty(data.EncryptedYouTubeApiKey) &&
                    root.TryGetProperty("YouTubeApiKey", out var ytApiKey))
                {
                    var value = ytApiKey.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        data.YouTubeApiKey = value;
                        migrated = true;
                        Log.Information("[SettingsStore] YouTube API Key migrate edildi");
                    }
                }

                // Instagram Session ID
                if (string.IsNullOrEmpty(data.EncryptedInstagramSessionId) &&
                    root.TryGetProperty("InstagramSessionId", out var igSession))
                {
                    var value = igSession.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        data.InstagramSessionId = value;
                        migrated = true;
                        Log.Information("[SettingsStore] Instagram Session ID migrate edildi");
                    }
                }

                if (migrated)
                {
                    Log.Information("[SettingsStore] Eski ayarlar şifreli formata migrate edildi");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[SettingsStore] Migration sırasında hata (yeni kurulumda normal)");
            }
        }

        private static void StartAutoSave()
        {
            // Her 30 saniyede bir kaydet (değişiklik varsa)
            _autoSaveTimer = new System.Threading.Timer(_ =>
            {
                lock (_lock)
                {
                    if (_isDirty)
                    {
                        Save();
                    }
                }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// Ayar verileri.
    /// </summary>
    public class SettingsData
    {
        // Video Ayarları
        public string VideoResolution { get; set; } = "1920x1080";
        public int VideoWidth { get; set; } = 1920;
        public int VideoHeight { get; set; } = 1080;
        public int VideoKbps { get; set; } = 4500;
        public int Fps { get; set; } = 30;
        public string VideoEncoder { get; set; } = "libx264";
        public string VideoPreset { get; set; } = "veryfast";

        // Audio Ayarları
        public int AudioKbps { get; set; } = 128;
        public string AudioEncoder { get; set; } = "aac";
        public int AudioSampleRate { get; set; } = 44100;
        public int AudioChannels { get; set; } = 2;

        // Cihaz Seçimleri
        public string SelectedCamera { get; set; } = "";
        public string SelectedMicrophone { get; set; } = "";
        public string SelectedDesktopAudio { get; set; } = "";
        public string SelectedScreen { get; set; } = "";
        public string CaptureMode { get; set; } = "Camera"; // "Camera", "Screen", "Both"

        // Platform Ayarları
        public string YouTubeVideoId { get; set; } = "";
        public string YouTubeChannelId { get; set; } = "";
        public string YouTubeRtmpUrl { get; set; } = "rtmp://a.rtmp.youtube.com/live2";

        [System.Text.Json.Serialization.JsonIgnore]
        public string YouTubeStreamKey
        {
            get => YouTubeStreamKeyDecrypted;
            set => YouTubeStreamKeyDecrypted = value;
        }

        public string TwitchUsername { get; set; } = "";
        public string TwitchRtmpUrl { get; set; } = "rtmp://live.twitch.tv/app";

        [System.Text.Json.Serialization.JsonIgnore]
        public string TwitchStreamKey
        {
            get => TwitchStreamKeyDecrypted;
            set => TwitchStreamKeyDecrypted = value;
        }

        // Twitch Chat Ayarları
        public string TwitchChannelName { get; set; } = "";
        public string TwitchBotUsername { get; set; } = "";

        // Twitch OAuth Token (şifreli)
        private string _encryptedTwitchOAuthToken = "";
        public string EncryptedTwitchOAuthToken
        {
            get => _encryptedTwitchOAuthToken;
            set => _encryptedTwitchOAuthToken = value;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string TwitchOAuthToken
        {
            get => SecretStore.Unprotect(_encryptedTwitchOAuthToken) ?? "";
            set => _encryptedTwitchOAuthToken = SecretStore.Protect(value) ?? "";
        }

        public string TikTokUsername { get; set; } = "";
        public string InstagramUsername { get; set; } = "";
        public string InstagramUserId { get => InstagramUsername; set => InstagramUsername = value; }

        // ===== INSTAGRAM CHAT ALANLARI =====

        /// <summary>
        /// Instagram okuyucu hesap şifresi (Private API için).
        /// ÖNEMLİ: Ana hesap yerine ayrı bir okuyucu hesap kullanın!
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string InstagramPassword
        {
            get => InstagramPasswordDecrypted;
            set => InstagramPasswordDecrypted = value;
        }

        /// <summary>
        /// Yayın yapan hesabın kullanıcı adı.
        /// Okuyucu hesap farklıysa burayı doldurun.
        /// </summary>
        public string InstagramBroadcasterUsername { get; set; } = "";

        /// <summary>
        /// Instagram Graph API Access Token (opsiyonel).
        /// Business/Creator hesap gerektirir.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string InstagramAccessToken
        {
            get => InstagramAccessTokenDecrypted;
            set => InstagramAccessTokenDecrypted = value;
        }

        /// <summary>
        /// Instagram API modu: "Hybrid" (Private + Graph birlikte)
        /// </summary>
        public string InstagramApiMode { get; set; } = "Hybrid";

        // ===== INSTAGRAM CHAT ALANLARI SONU =====

        [System.Text.Json.Serialization.JsonIgnore]
        public string InstagramSessionId
        {
            get => InstagramSessionIdDecrypted;
            set => InstagramSessionIdDecrypted = value;
        }

        public string FacebookPageId { get; set; } = "";

        [System.Text.Json.Serialization.JsonIgnore]
        public string FacebookStreamKey
        {
            get => FacebookStreamKeyDecrypted;
            set => FacebookStreamKeyDecrypted = value;
        }

        public string FacebookLiveVideoId { get; set; } = "";
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
        public bool EnableLocalRecord { get => RecordingEnabled; set => RecordingEnabled = value; }
        public string RecordFolder { get => RecordingPath; set => RecordingPath = value; }

        // API Keys - ŞİFRELENMİŞ SAKLANIYOR (SecretStore ile DPAPI)
        // JSON'a şifreli olarak yazılır, okunurken çözülür

        // YouTube API Key
        private string _encryptedYouTubeApiKey = "";
        public string EncryptedYouTubeApiKey
        {
            get => _encryptedYouTubeApiKey;
            set => _encryptedYouTubeApiKey = value;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string YouTubeApiKey
        {
            get => SecretStore.Unprotect(_encryptedYouTubeApiKey) ?? "";
            set => _encryptedYouTubeApiKey = SecretStore.Protect(value) ?? "";
        }

        // Twitch Client ID
        private string _encryptedTwitchClientId = "";
        public string EncryptedTwitchClientId
        {
            get => _encryptedTwitchClientId;
            set => _encryptedTwitchClientId = value;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string TwitchClientId
        {
            get => SecretStore.Unprotect(_encryptedTwitchClientId) ?? "";
            set => _encryptedTwitchClientId = SecretStore.Protect(value) ?? "";
        }

        // Facebook Access Token
        private string _encryptedFacebookAccessToken = "";
        public string EncryptedFacebookAccessToken
        {
            get => _encryptedFacebookAccessToken;
            set => _encryptedFacebookAccessToken = value;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string FacebookAccessToken
        {
            get => SecretStore.Unprotect(_encryptedFacebookAccessToken) ?? "";
            set => _encryptedFacebookAccessToken = SecretStore.Protect(value) ?? "";
        }

        // Stream Keys - BUNLAR DA ŞİFRELENMELİ!
        private string _encryptedYouTubeStreamKey = "";
        public string EncryptedYouTubeStreamKey
        {
            get => _encryptedYouTubeStreamKey;
            set => _encryptedYouTubeStreamKey = value;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string YouTubeStreamKeyDecrypted
        {
            get => SecretStore.Unprotect(_encryptedYouTubeStreamKey) ?? "";
            set => _encryptedYouTubeStreamKey = SecretStore.Protect(value) ?? "";
        }

        private string _encryptedTwitchStreamKey = "";
        public string EncryptedTwitchStreamKey
        {
            get => _encryptedTwitchStreamKey;
            set => _encryptedTwitchStreamKey = value;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string TwitchStreamKeyDecrypted
        {
            get => SecretStore.Unprotect(_encryptedTwitchStreamKey) ?? "";
            set => _encryptedTwitchStreamKey = SecretStore.Protect(value) ?? "";
        }

        private string _encryptedFacebookStreamKey = "";
        public string EncryptedFacebookStreamKey
        {
            get => _encryptedFacebookStreamKey;
            set => _encryptedFacebookStreamKey = value;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string FacebookStreamKeyDecrypted
        {
            get => SecretStore.Unprotect(_encryptedFacebookStreamKey) ?? "";
            set => _encryptedFacebookStreamKey = SecretStore.Protect(value) ?? "";
        }

        private string _encryptedInstagramSessionId = "";
        public string EncryptedInstagramSessionId
        {
            get => _encryptedInstagramSessionId;
            set => _encryptedInstagramSessionId = value;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string InstagramSessionIdDecrypted
        {
            get => SecretStore.Unprotect(_encryptedInstagramSessionId) ?? "";
            set => _encryptedInstagramSessionId = SecretStore.Protect(value) ?? "";
        }

        // ===== INSTAGRAM ENCRYPTED ALANLARI =====

        private string _encryptedInstagramPassword = "";
        public string EncryptedInstagramPassword
        {
            get => _encryptedInstagramPassword;
            set => _encryptedInstagramPassword = value;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string InstagramPasswordDecrypted
        {
            get => SecretStore.Unprotect(_encryptedInstagramPassword) ?? "";
            set => _encryptedInstagramPassword = SecretStore.Protect(value) ?? "";
        }

        private string _encryptedInstagramAccessToken = "";
        public string EncryptedInstagramAccessToken
        {
            get => _encryptedInstagramAccessToken;
            set => _encryptedInstagramAccessToken = value;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string InstagramAccessTokenDecrypted
        {
            get => SecretStore.Unprotect(_encryptedInstagramAccessToken) ?? "";
            set => _encryptedInstagramAccessToken = SecretStore.Protect(value) ?? "";
        }

        private string _encryptedFacebookPageAccessToken = "";
        public string EncryptedFacebookPageAccessToken
        {
            get => _encryptedFacebookPageAccessToken;
            set => _encryptedFacebookPageAccessToken = value;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string FacebookPageAccessToken
        {
            get => SecretStore.Unprotect(_encryptedFacebookPageAccessToken) ?? "";
            set => _encryptedFacebookPageAccessToken = SecretStore.Protect(value) ?? "";
        }

        // ===== INSTAGRAM ENCRYPTED ALANLARI SONU =====

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