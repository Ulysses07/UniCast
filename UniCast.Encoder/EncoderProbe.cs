using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace UniCast.Encoder
{
    /// <summary>
    /// FFmpeg üzerinden mevcut encoder'ları tespit eder (NVENC/QSV/AMF/libx264).
    /// Tek sefer çalıştırmaya uygundur; Start sırasında çağrılır.
    /// </summary>
    public sealed class EncoderProbe
    {
        public bool HasNvenc { get; private set; }
        public bool HasQsv { get; private set; }
        public bool HasAmf { get; private set; }
        public bool HasX264 { get; private set; }

        public static EncoderProbe Run()
        {
            var probe = new EncoderProbe();
            var exe = FfmpegProcess.ResolveFfmpegPath();

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-hide_banner -encoders",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };

            try
            {
                using var p = Process.Start(psi)!;
                var err = p.StandardError.ReadToEnd();
                var stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit();

                var text = (stdout + "\n" + err).ToLowerInvariant();

                // Tipik çıktıda "V..... h264_nvenc" gibi satırlar olur
                probe.HasNvenc = Regex.IsMatch(text, @"\bh264_nvenc\b");
                probe.HasQsv = Regex.IsMatch(text, @"\bh264_qsv\b");
                probe.HasAmf = Regex.IsMatch(text, @"\bh264_amf\b");
                probe.HasX264 = Regex.IsMatch(text, @"\blibx264\b") || Regex.IsMatch(text, @"\bh264\b");

                return probe;
            }
            catch
            {
                // FFmpeg bulunamadıysa/yürütülemediyse, en az libx264 varsayımı
                probe.HasX264 = true;
                return probe;
            }
        }
    }
}
