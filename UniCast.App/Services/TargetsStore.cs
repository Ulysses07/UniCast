using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UniCast.Core.Models;

namespace UniCast.App.Services
{
    public static class TargetsStore
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UniCast");
        private static readonly string FilePath = Path.Combine(Dir, "targets.json");

        public static List<TargetItem> Load()
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                if (!File.Exists(FilePath)) return new List<TargetItem>();
                var json = File.ReadAllText(FilePath);
                var list = JsonSerializer.Deserialize<List<TargetItem>>(json) ?? new List<TargetItem>();
                return list;
            }
            catch { return new List<TargetItem>(); }
        }

        public static void Save(IReadOnlyCollection<TargetItem> items)
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
    }
}
