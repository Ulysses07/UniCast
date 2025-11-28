using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using UniCast.Core.Settings;
using UniCast.App.Security;

namespace UniCast.App.Services
{
    public static class SettingsStore
    {
        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UniCast");

        private static readonly string FilePath = Path.Combine(Dir, "settings.json");

        private static readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);

        private const int MAX_RETRY_ATTEMPTS = 3;
        private const int RETRY_DELAY_MS = 100;

        private sealed class PersistModel
        {
            // Plain alanlar
            public bool ShowOverlay { get; set; }
            public int OverlayX { get; set; }
            public int OverlayY { get; set; }
            public double OverlayOpacity { get; set; }
            public int OverlayFontSize { get; set; }

            // DÜZELTME: Eksik olan OverlayWidth ve OverlayHeight eklendi
            public double OverlayWidth { get; set; } = 300;
            public double OverlayHeight { get; set; } = 400;

            public string YouTubeChannelId { get; set; } = "";
            public string TikTokRoomId { get; set; } = "";

            // Encrypted alanlar (base64)
            public string YouTubeApiKeyEnc { get; set; } = "";
            public string TikTokSessionCookieEnc { get; set; } = "";
            public string FacebookPageId { get; set; } = "";
            public string FacebookLiveVideoId { get; set; } = "";
            public string FacebookAccessTokenEnc { get; set; } = "";

            // Encoder/Quality alanları
            public string Encoder { get; set; } = "auto";
            public int VideoKbps { get; set; } = 3500;
            public int AudioKbps { get; set; } = 160;
            public int AudioDelayMs { get; set; } = 0;
            public int Fps { get; set; } = 30;
            public int Width { get; set; } = 1280;
            public int Height { get; set; } = 720;
            public string DefaultCamera { get; set; } = "";
            public string DefaultMicrophone { get; set; } = "";
            public string RecordFolder { get; set; } = "";
            public bool EnableLocalRecord { get; set; } = false;

            // Instagram
            public string InstagramUserId { get; set; } = "";
            public string InstagramSessionIdEnc { get; set; } = "";
        }

        public static SettingsData Load()
        {
            _lock.EnterReadLock();
            try
            {
                return LoadInternal();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public static void Save(SettingsData s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));

            _lock.EnterWriteLock();
            try
            {
                SaveInternalWithRetry(s);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public static void Update(Action<SettingsData> modifier)
        {
            if (modifier == null) throw new ArgumentNullException(nameof(modifier));

            _lock.EnterWriteLock();
            try
            {
                var data = LoadInternal();
                modifier(data);
                SaveInternalWithRetry(data);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        private static SettingsData LoadInternal()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return CreateDefaultSettings();

                string json;
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new StreamReader(fs))
                {
                    json = reader.ReadToEnd();
                }

                if (string.IsNullOrWhiteSpace(json))
                    return CreateDefaultSettings();

                var p = JsonSerializer.Deserialize<PersistModel>(json);
                if (p == null)
                    return CreateDefaultSettings();

                var s = MapToSettingsData(p);
                s.Normalize();
                return s;
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] JSON parse hatası: {ex.Message}");
                BackupCorruptedFile();
                return CreateDefaultSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Yükleme hatası: {ex.Message}");
                return CreateDefaultSettings();
            }
        }

        private static void SaveInternalWithRetry(SettingsData s)
        {
            Exception? lastException = null;

            for (int attempt = 0; attempt < MAX_RETRY_ATTEMPTS; attempt++)
            {
                try
                {
                    SaveInternal(s);
                    return;
                }
                catch (IOException ex)
                {
                    lastException = ex;
                    System.Diagnostics.Debug.WriteLine($"[Settings] Yazma hatası (deneme {attempt + 1}): {ex.Message}");

                    if (attempt < MAX_RETRY_ATTEMPTS - 1)
                    {
                        Thread.Sleep(RETRY_DELAY_MS * (attempt + 1));
                    }
                }
            }

            throw new IOException($"Ayarlar kaydedilemedi ({MAX_RETRY_ATTEMPTS} deneme sonrası)", lastException);
        }

        private static void SaveInternal(SettingsData s)
        {
            if (!Directory.Exists(Dir))
                Directory.CreateDirectory(Dir);

            var p = MapToPersistModel(s);

            var json = JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true });

            var tempPath = FilePath + ".tmp";
            var backupPath = FilePath + ".bak";

            try
            {
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs))
                {
                    writer.Write(json);
                    writer.Flush();
                    fs.Flush(true);
                }

                if (File.Exists(FilePath))
                {
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                    File.Move(FilePath, backupPath);
                }

                File.Move(tempPath, FilePath);
            }
            catch
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
                throw;
            }
        }

        private static void BackupCorruptedFile()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var corruptPath = FilePath + $".corrupt_{DateTime.Now:yyyyMMdd_HHmmss}";
                    File.Move(FilePath, corruptPath);
                    System.Diagnostics.Debug.WriteLine($"[Settings] Bozuk dosya yedeklendi: {corruptPath}");
                }
            }
            catch { }
        }

        private static SettingsData CreateDefaultSettings()
        {
            var s = new SettingsData();
            s.Normalize();
            return s;
        }

        private static SettingsData MapToSettingsData(PersistModel p)
        {
            return new SettingsData
            {
                // Chat & Overlay
                ShowOverlay = p.ShowOverlay,
                OverlayX = p.OverlayX,
                OverlayY = p.OverlayY,
                OverlayOpacity = p.OverlayOpacity,
                OverlayFontSize = p.OverlayFontSize,

                // DÜZELTME: OverlayWidth ve OverlayHeight mapping eklendi
                OverlayWidth = p.OverlayWidth,
                OverlayHeight = p.OverlayHeight,

                YouTubeChannelId = p.YouTubeChannelId ?? "",
                TikTokRoomId = p.TikTokRoomId ?? "",

                // Secrets (unprotect)
                YouTubeApiKey = SecretStore.Unprotect(p.YouTubeApiKeyEnc) ?? "",
                TikTokSessionCookie = SecretStore.Unprotect(p.TikTokSessionCookieEnc) ?? "",
                FacebookPageId = p.FacebookPageId ?? "",
                FacebookLiveVideoId = p.FacebookLiveVideoId ?? "",
                FacebookAccessToken = SecretStore.Unprotect(p.FacebookAccessTokenEnc) ?? "",

                // Encoding / General
                Encoder = p.Encoder ?? "auto",
                VideoKbps = p.VideoKbps,
                AudioKbps = p.AudioKbps,
                AudioDelayMs = p.AudioDelayMs,
                Fps = p.Fps,
                Width = p.Width,
                Height = p.Height,
                DefaultCamera = p.DefaultCamera ?? "",
                DefaultMicrophone = p.DefaultMicrophone ?? "",
                RecordFolder = p.RecordFolder ?? "",
                EnableLocalRecord = p.EnableLocalRecord,

                // Instagram
                InstagramUserId = p.InstagramUserId ?? "",
                InstagramSessionId = SecretStore.Unprotect(p.InstagramSessionIdEnc) ?? ""
            };
        }

        private static PersistModel MapToPersistModel(SettingsData s)
        {
            return new PersistModel
            {
                // Chat & Overlay
                ShowOverlay = s.ShowOverlay,
                OverlayX = s.OverlayX,
                OverlayY = s.OverlayY,
                OverlayOpacity = s.OverlayOpacity,
                OverlayFontSize = s.OverlayFontSize,

                // DÜZELTME: OverlayWidth ve OverlayHeight mapping eklendi
                OverlayWidth = s.OverlayWidth,
                OverlayHeight = s.OverlayHeight,

                YouTubeChannelId = s.YouTubeChannelId ?? "",
                TikTokRoomId = s.TikTokRoomId ?? "",

                // Secrets (protect)
                YouTubeApiKeyEnc = SecretStore.Protect(s.YouTubeApiKey ?? "") ?? "",
                TikTokSessionCookieEnc = SecretStore.Protect(s.TikTokSessionCookie ?? "") ?? "",
                InstagramUserId = s.InstagramUserId ?? "",
                InstagramSessionIdEnc = SecretStore.Protect(s.InstagramSessionId ?? "") ?? "",
                FacebookPageId = s.FacebookPageId ?? "",
                FacebookLiveVideoId = s.FacebookLiveVideoId ?? "",
                FacebookAccessTokenEnc = SecretStore.Protect(s.FacebookAccessToken ?? "") ?? "",

                // Encoding / General
                Encoder = s.Encoder ?? "auto",
                VideoKbps = s.VideoKbps,
                AudioKbps = s.AudioKbps,
                AudioDelayMs = s.AudioDelayMs,
                Fps = s.Fps,
                Width = s.Width,
                Height = s.Height,
                DefaultCamera = s.DefaultCamera ?? "",
                DefaultMicrophone = s.DefaultMicrophone ?? "",
                RecordFolder = s.RecordFolder ?? "",
                EnableLocalRecord = s.EnableLocalRecord
            };
        }
    }
}