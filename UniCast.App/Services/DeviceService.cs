using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using UniCast.Encoder;

namespace UniCast.App.Services
{
    public interface IDeviceService
    {
        (IReadOnlyList<string> video, IReadOnlyList<string> audio) ListDevices();
    }


    public sealed class DeviceService : IDeviceService
    {
        private static string ResolveFfmpegPath()
        {
            var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            return System.IO.File.Exists(exe) ? exe : "ffmpeg";
        }
        public static (string[] video, string[] audio) ListDshowDevices()
            => FfmpegProcess.ListDshowDevices();

        public (IReadOnlyList<string> video, IReadOnlyList<string> audio) ListDevices()
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveFfmpegPath(),
                Arguments = "-hide_banner -f dshow -list_devices true -i dummy",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var p = Process.Start(psi)!;
            var output = (p.StandardError.ReadToEnd() + "\n" + p.StandardOutput.ReadToEnd()).Replace("\r", "");
            p.WaitForExit();

            var video = new List<string>();
            var audio = new List<string>();

            // FFmpeg çıktısındaki tipik kalıplar:
            // [dshow @ ...] "Integrated Camera"
            // [dshow @ ...] "Microphone (Realtek ...)"
            var re = new Regex(@"^\[dshow @ .*?\]\s+""(?<name>.+)""$", RegexOptions.Multiline);
            var currentSection = "";

            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("DirectShow video devices"))
                    currentSection = "video";
                else if (line.Contains("DirectShow audio devices"))
                    currentSection = "audio";
                else
                {
                    var m = re.Match(line);
                    if (m.Success)
                    {
                        var name = m.Groups["name"].Value.Trim();
                        if (currentSection == "video") video.Add(name);
                        else if (currentSection == "audio") audio.Add(name);
                    }
                }
            }

            return (video, audio);
        }
    }
}
