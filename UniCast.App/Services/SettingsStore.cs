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
        /// <summary>
        /// Ayarları günceller.
        /// DÜZELTME v17.3: Nested lock önlendi - Data erişimi lock dışında.
        /// </summary>
        public static void Update(Action<SettingsData> updateAction)
        {
            // DÜZELTME: Data property'sine erişim lock DIŞINDA
            // Data property kendi lock'unu alır, içeride tekrar lock almak deadlock yaratır
            var data = Data;

            lock (_lock)
            {
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

        // Video ayarları için alias property'ler (eski kod uyumluluğu)
        [System.Text.Json.Serialization.JsonIgnore]
        public int Width { get => VideoWidth; set => VideoWidth = value; }

        [System.Text.Json.Serialization.JsonIgnore]
        public int Height { get => VideoHeight; set => VideoHeight = value; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string Encoder { get => VideoEncoder; set => VideoEncoder = value; }

        // Audio Ayarları
        public int AudioKbps { get; set; } = 128;
        public string AudioEncoder { get; set; } = "aac";
        public int AudioSampleRate { get; set; } = 44100;
        public int AudioChannels { get; set; } = 2;
        public int AudioDelayMs { get; set; } = 0;

        // Cihaz Seçimleri
        public string SelectedCamera { get; set; } = "";
        public string SelectedMicrophone { get; set; } = "";

        // Cihaz alias'ları (eski kod uyumluluğu)
        [System.Text.Json.Serialization.JsonIgnore]
        public string DefaultCamera { get => SelectedCamera; set => SelectedCamera = value; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string DefaultMicrophone { get => SelectedMicrophone; set => SelectedMicrophone = value; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string SelectedVideoDevice { get => SelectedCamera; set => SelectedCamera = value; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string SelectedAudioDevice { get => SelectedMicrophone; set => SelectedMicrophone = value; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string VideoDevice { get => SelectedCamera; set => SelectedCamera = value; }

        // Scene/Overlay Items
        public List<OverlayItem> SceneItems { get; set; } = new();
        public string SelectedDesktopAudio { get; set; } = "";
        public string SelectedScreen { get; set; } = "";
        public string CaptureMode { get; set; } = "Camera"; // "Camera", "Screen", "Both"

        // Platform Ayarları
        public string YouTubeVideoId { get; set; } = "";
        public string YouTubeChannelId { get; set; } = "";
        public string YouTubeRtmpUrl { get; set; } = "rtmp://a.rtmp.youtube.com/live2";

        // YouTubeStreamKey - Tam tanım aşağıda (Stream Keys bölümünde)

        public string TwitchUsername { get; set; } = "";
        public string TwitchRtmpUrl { get; set; } = "rtmp://live.twitch.tv/app";

        // TwitchStreamKey - Tam tanım aşağıda (Stream Keys bölümünde)

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

        // InstagramPassword - Tam tanım aşağıda (Instagram Encrypted Alanları bölümünde)

        /// <summary>
        /// Yayın yapan hesabın kullanıcı adı.
        /// Okuyucu hesap farklıysa burayı doldurun.
        /// </summary>
        public string InstagramBroadcasterUsername { get; set; } = "";

        // InstagramAccessToken - Tam tanım aşağıda (Instagram Encrypted Alanları bölümünde)

        /// <summary>
        /// Instagram API modu: "Hybrid" (Private + Graph birlikte)
        /// </summary>
        public string InstagramApiMode { get; set; } = "Hybrid";

        // ===== INSTAGRAM CHAT ALANLARI SONU =====

        // InstagramSessionId - Tam tanım aşağıda (Instagram Session ID bölümünde)

        public string FacebookPageId { get; set; } = "";

        // FacebookStreamKey - Tam tanım aşağıda (Facebook Stream Key bölümünde)

        public string FacebookLiveVideoId { get; set; } = "";

        // Yeni WebView2 tabanlı Facebook ayarları
        public string FacebookCookies { get; set; } = "";
        public string FacebookUserId { get; set; } = "";
        public string FacebookLiveVideoUrl { get; set; } = "";

        public string CustomRtmpUrl { get; set; } = "";

        // Layout Ayarları (ControlView panel genişlikleri)
        public double LayoutMolaColumnWidth { get; set; } = 120;
        public double LayoutCameraColumnWidth { get; set; } = 350;
        public double LayoutChatColumnWidth { get; set; } = 300;

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

        // Stream Keys - ŞİFRELENMİŞ SAKLANIYOR (SecretStore ile DPAPI)
        // DÜZELTME v27: Tutarlı naming convention
        // - Encrypted{X} -> JSON'a yazılır (şifreli)
        // - {X} -> Kod içinde kullanılır (decrypt edilmiş)
        // - {X}Decrypted -> DEPRECATED, geriye uyumluluk için

        // ===== YOUTUBE STREAM KEY =====
        private string _encryptedYouTubeStreamKey = "";
        public string EncryptedYouTubeStreamKey
        {
            get => _encryptedYouTubeStreamKey;
            set => _encryptedYouTubeStreamKey = value;
        }

        /// <summary>
        /// YouTube Stream Key (decrypted).
        /// Kullanım: var key = settings.YouTubeStreamKey;
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string YouTubeStreamKey
        {
            get => SecretStore.Unprotect(_encryptedYouTubeStreamKey) ?? "";
            set => _encryptedYouTubeStreamKey = SecretStore.Protect(value) ?? "";
        }

        /// <summary>
        /// DEPRECATED: YouTubeStreamKey kullanın.
        /// Geriye uyumluluk için bırakıldı.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        [Obsolete("Use YouTubeStreamKey instead")]
        public string YouTubeStreamKeyDecrypted
        {
            get => YouTubeStreamKey;
            set => YouTubeStreamKey = value;
        }

        // ===== TWITCH STREAM KEY =====
        private string _encryptedTwitchStreamKey = "";
        public string EncryptedTwitchStreamKey
        {
            get => _encryptedTwitchStreamKey;
            set => _encryptedTwitchStreamKey = value;
        }

        /// <summary>
        /// Twitch Stream Key (decrypted).
        /// Kullanım: var key = settings.TwitchStreamKey;
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string TwitchStreamKey
        {
            get => SecretStore.Unprotect(_encryptedTwitchStreamKey) ?? "";
            set => _encryptedTwitchStreamKey = SecretStore.Protect(value) ?? "";
        }

        /// <summary>
        /// DEPRECATED: TwitchStreamKey kullanın.
        /// Geriye uyumluluk için bırakıldı.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        [Obsolete("Use TwitchStreamKey instead")]
        public string TwitchStreamKeyDecrypted
        {
            get => TwitchStreamKey;
            set => TwitchStreamKey = value;
        }

        // ===== FACEBOOK STREAM KEY =====
        private string _encryptedFacebookStreamKey = "";
        public string EncryptedFacebookStreamKey
        {
            get => _encryptedFacebookStreamKey;
            set => _encryptedFacebookStreamKey = value;
        }

        /// <summary>
        /// Facebook Stream Key (decrypted).
        /// Kullanım: var key = settings.FacebookStreamKey;
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string FacebookStreamKey
        {
            get => SecretStore.Unprotect(_encryptedFacebookStreamKey) ?? "";
            set => _encryptedFacebookStreamKey = SecretStore.Protect(value) ?? "";
        }

        /// <summary>
        /// DEPRECATED: FacebookStreamKey kullanın.
        /// Geriye uyumluluk için bırakıldı.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        [Obsolete("Use FacebookStreamKey instead")]
        public string FacebookStreamKeyDecrypted
        {
            get => FacebookStreamKey;
            set => FacebookStreamKey = value;
        }

        // ===== INSTAGRAM SESSION ID =====
        private string _encryptedInstagramSessionId = "";
        public string EncryptedInstagramSessionId
        {
            get => _encryptedInstagramSessionId;
            set => _encryptedInstagramSessionId = value;
        }

        /// <summary>
        /// Instagram Session ID (decrypted).
        /// Kullanım: var sessionId = settings.InstagramSessionId;
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string InstagramSessionId
        {
            get => SecretStore.Unprotect(_encryptedInstagramSessionId) ?? "";
            set => _encryptedInstagramSessionId = SecretStore.Protect(value) ?? "";
        }

        /// <summary>
        /// DEPRECATED: InstagramSessionId kullanın.
        /// Geriye uyumluluk için bırakıldı.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        [Obsolete("Use InstagramSessionId instead")]
        public string InstagramSessionIdDecrypted
        {
            get => InstagramSessionId;
            set => InstagramSessionId = value;
        }

        // ===== INSTAGRAM ENCRYPTED ALANLARI =====

        private string _encryptedInstagramPassword = "";
        public string EncryptedInstagramPassword
        {
            get => _encryptedInstagramPassword;
            set => _encryptedInstagramPassword = value;
        }

        /// <summary>
        /// Instagram Password (decrypted).
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string InstagramPassword
        {
            get => SecretStore.Unprotect(_encryptedInstagramPassword) ?? "";
            set => _encryptedInstagramPassword = SecretStore.Protect(value) ?? "";
        }

        /// <summary>
        /// DEPRECATED: InstagramPassword kullanın.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        [Obsolete("Use InstagramPassword instead")]
        public string InstagramPasswordDecrypted
        {
            get => InstagramPassword;
            set => InstagramPassword = value;
        }

        private string _encryptedInstagramAccessToken = "";
        public string EncryptedInstagramAccessToken
        {
            get => _encryptedInstagramAccessToken;
            set => _encryptedInstagramAccessToken = value;
        }

        /// <summary>
        /// Instagram Access Token (decrypted).
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string InstagramAccessToken
        {
            get => SecretStore.Unprotect(_encryptedInstagramAccessToken) ?? "";
            set => _encryptedInstagramAccessToken = SecretStore.Protect(value) ?? "";
        }

        /// <summary>
        /// DEPRECATED: InstagramAccessToken kullanın.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        [Obsolete("Use InstagramAccessToken instead")]
        public string InstagramAccessTokenDecrypted
        {
            get => InstagramAccessToken;
            set => InstagramAccessToken = value;
        }

        private string _encryptedFacebookPageAccessToken = "";
        public string EncryptedFacebookPageAccessToken
        {
            get => _encryptedFacebookPageAccessToken;
            set => _encryptedFacebookPageAccessToken = value;
        }

        /// <summary>
        /// Facebook Page Access Token (decrypted).
        /// </summary>
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

            // Layout (minimum genişlikler)
            LayoutMolaColumnWidth = Math.Clamp(LayoutMolaColumnWidth, 100, 300);
            LayoutCameraColumnWidth = Math.Clamp(LayoutCameraColumnWidth, 200, 800);
            LayoutChatColumnWidth = Math.Clamp(LayoutChatColumnWidth, 200, 600);

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