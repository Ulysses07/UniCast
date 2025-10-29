using System;
using System.IO;
using System.Text.Json;

namespace UniCast.App.Services
{
    /// <summary>
    /// JSON tabanlı kalıcı ayar depolayıcı.
    /// AppData\UniCast\settings.json dosyasını kullanır.
    /// </summary>
    public static class SettingsStore
    {
        private static readonly string SettingsDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UniCast");
        private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

        public static SettingsData Load()
        {
            try
            {
                if (!File.Exists(SettingsFile))
                    return new SettingsData(); // Varsayılan

                var json = File.ReadAllText(SettingsFile);
                var data = JsonSerializer.Deserialize<SettingsData>(json);
                return data ?? new SettingsData();
            }
            catch
            {
                return new SettingsData();
            }
        }

        public static void Save(SettingsData data)
        {
            try
            {
                if (!Directory.Exists(SettingsDir))
                    Directory.CreateDirectory(SettingsDir);

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFile, json);
            }
            catch
            {
                // kaydetme hataları sessiz geçilir
            }
        }
    }

    /// <summary>
    /// JSON’a yazılan ayar modeli.
    /// </summary>
    public sealed class SettingsData
    {
        public int VideoKbps { get; set; } = 3500;
        public int Fps { get; set; } = 30;
        public string RecordFolder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "UniCast");
    }
}
