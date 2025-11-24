namespace UniCast.Core.Models
{
    public class CaptureDevice
    {
        public string Name { get; set; } = "";
        public string Id { get; set; } = ""; // Bu ID sayesinde cihaz karışıklığı biter

        public override string ToString() => Name;
    }
}