using System.Text.Json;

namespace UniCast.Config;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true
    };

    private static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UniCast");

    private static string FilePath => Path.Combine(AppDataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, _jsonOpts) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(AppDataDir);
        var json = JsonSerializer.Serialize(s, _jsonOpts);
        File.WriteAllText(FilePath, json);
    }
}
