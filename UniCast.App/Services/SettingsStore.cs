using System;
using System.IO;
using System.Text.Json;
using UniCast.Core.Settings;

namespace UniCast.App.Services
{
    public static class SettingsStore
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UniCast");
        private static readonly string FilePath = Path.Combine(Dir, "settings.json");

        public static SettingsData Load()
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                if (!File.Exists(FilePath)) { var def = new SettingsData(); Save(def); return def; }
                var json = File.ReadAllText(FilePath);
                var data = JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
                Sanitize(data);
                return data;
            }
            catch
            {
                return new SettingsData();
            }
        }

        public static void Save(SettingsData d)
        {
            Sanitize(d);
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }

        private static void Sanitize(SettingsData d)
        {
            d.Encoder ??= "auto";
            d.DefaultCamera ??= "";
            d.DefaultMicrophone ??= "";
            d.RecordFolder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "UniCast");

            if (d.VideoKbps <= 0) d.VideoKbps = 3500;
            if (d.AudioKbps <= 0) d.AudioKbps = 160;
            if (d.Fps is < 10 or > 60) d.Fps = 30;
            if (d.Width <= 0) d.Width = 1280;
            if (d.Height <= 0) d.Height = 720;

            Directory.CreateDirectory(d.RecordFolder);
        }
    }
}
