using System;
using System.IO;

namespace UniCast.Core.Settings
{
    public sealed class SettingsData
    {
        public string DefaultCamera { get; set; } = "";
        public string DefaultMicrophone { get; set; } = "";

        public string Encoder { get; set; } = "auto";
        public int VideoKbps { get; set; } = 3500;
        public int AudioKbps { get; set; } = 160;
        public int Fps { get; set; } = 30;

        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;

        public bool EnableLocalRecord { get; set; } = false;
        public string RecordFolder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "UniCast");
    }
}
