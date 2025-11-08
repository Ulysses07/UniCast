namespace UniCast.Core.Models
{
    public sealed class Profile
    {
        public string Name { get; set; } = "Default";
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public int Fps { get; set; } = 30;

        public static Profile Default() => new Profile();

        public static Profile GetByName(string? name, IEnumerable<Profile>? list)
        {
            if (string.IsNullOrWhiteSpace(name)) return list?.FirstOrDefault() ?? Default();
            var l = list ?? Array.Empty<Profile>();
            return l.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Default();
        }
    }
}
