namespace UniCast.Core.Models
{
    public class CaptureDevice
    {
        public string Name { get; set; } = "";
        public string Id { get; set; } = ""; // DevicePath (SymLink)

        // UI'da ComboBox içinde düzgün görünmesi için
        public override string ToString() => Name;
    }
}