using System;
using System.IO;
using System.Text.Json;

namespace UniCast.Core
{
    public sealed class AppSettings
    {
        public string Theme { get; set; } = "Sistem";      // Sistem | Acik | Koyu
        public string VideoEncoder { get; set; } = "Otomatik"; // Otomatik | NVENC | AMF | QSV | libx264
        public int AudioKbps { get; set; } = 128;
    }

    public static class SettingsStorage
    {
        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true
        };

        public static string GetConfigPath()
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(root, "UniCast");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }

        public static AppSettings Load()
        {
            try
            {
                var path = GetConfigPath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var obj = JsonSerializer.Deserialize<AppSettings>(json, _jsonOpts);
                    if (obj != null) return obj;
                }
            }
            catch (Exception ex)
            {
                // DÜZELTME v25: Boş catch'e loglama eklendi
                System.Diagnostics.Debug.WriteLine($"[SettingsStorage] Load hatası, varsayılana dönülüyor: {ex.Message}");
            }

            return new AppSettings(); // defaults
        }

        public static void Save(AppSettings settings)
        {
            var path = GetConfigPath();
            var json = JsonSerializer.Serialize(settings, _jsonOpts);
            File.WriteAllText(path, json);
        }
    }
}