using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UniCast.Encoder.Hardware
{
    /// <summary>
    /// Hardware video decoding service.
    /// NVDEC (NVIDIA), QSV (Intel), D3D11VA/DXVA2 (Windows) desteği.
    /// 
    /// Kullanım alanları:
    /// - Video dosyası import
    /// - RTMP/HLS input decode
    /// - Screen recording playback
    /// - Picture-in-picture kaynakları
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class HardwareDecodeService : IDisposable
    {
        #region Singleton

        private static readonly Lazy<HardwareDecodeService> _instance =
            new(() => new HardwareDecodeService(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static HardwareDecodeService Instance => _instance.Value;

        #endregion

        #region Properties

        public IReadOnlyList<HardwareDecoder> AvailableDecoders => _decoders.AsReadOnly();
        public HardwareDecoder? BestDecoder => _decoders.Count > 0 ? _decoders[0] : null;
        public bool IsHardwareDecodingAvailable => _decoders.Count > 0;
        public bool IsDetectionComplete { get; private set; }

        #endregion

        #region Fields

        private readonly List<HardwareDecoder> _decoders = new();
        private readonly object _lock = new();
        private bool _disposed;

        #endregion

        #region Constructor

        private HardwareDecodeService() { }

        #endregion

        #region Detection

        /// <summary>
        /// Hardware decoder'ları tespit et
        /// </summary>
        public async Task<IReadOnlyList<HardwareDecoder>> DetectDecodersAsync(
            string? ffmpegPath = null,
            CancellationToken ct = default)
        {
            if (IsDetectionComplete)
                return _decoders.AsReadOnly();

            lock (_lock)
            {
                if (IsDetectionComplete)
                    return _decoders.AsReadOnly();

                _decoders.Clear();
            }

            try
            {
                // FFmpeg'den decoder listesini al
                var ffmpegDecoders = await GetFfmpegDecodersAsync(ffmpegPath, ct);

                // GPU bilgisini al
                var gpuVendor = await DetectGpuVendorAsync();

                // NVIDIA NVDEC
                if (gpuVendor == GpuVendor.Nvidia)
                {
                    await DetectNvdecAsync(ffmpegDecoders, ct);
                }

                // Intel QuickSync
                if (gpuVendor == GpuVendor.Intel || HasIntelIntegrated())
                {
                    await DetectQsvDecodeAsync(ffmpegDecoders, ct);
                }

                // D3D11VA (Windows genel)
                await DetectD3D11VAAsync(ffmpegDecoders, ct);

                // DXVA2 (eski Windows)
                await DetectDXVA2Async(ffmpegDecoders, ct);

                // Performans sırasına göre sırala
                lock (_lock)
                {
                    _decoders.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                }

                IsDetectionComplete = true;

                Debug.WriteLine($"[HardwareDecoder] Tespit edilen decoder sayısı: {_decoders.Count}");
                foreach (var dec in _decoders)
                {
                    Debug.WriteLine($"  - {dec.Name} ({dec.Type}) Priority: {dec.Priority}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HardwareDecoder] Detection hatası: {ex.Message}");
                IsDetectionComplete = true;
            }

            return _decoders.AsReadOnly();
        }

        private async Task DetectNvdecAsync(HashSet<string> ffmpegDecoders, CancellationToken ct)
        {
            var nvdecCodecs = new[]
            {
                ("h264_cuvid", "NVIDIA NVDEC H.264", HardwareDecoderType.NvdecH264, 100),
                ("hevc_cuvid", "NVIDIA NVDEC HEVC", HardwareDecoderType.NvdecHevc, 95),
                ("av1_cuvid", "NVIDIA NVDEC AV1", HardwareDecoderType.NvdecAv1, 90),
                ("vp9_cuvid", "NVIDIA NVDEC VP9", HardwareDecoderType.NvdecVp9, 85),
                ("mpeg2_cuvid", "NVIDIA NVDEC MPEG-2", HardwareDecoderType.NvdecMpeg2, 70),
                ("mpeg4_cuvid", "NVIDIA NVDEC MPEG-4", HardwareDecoderType.NvdecMpeg4, 65)
            };

            foreach (var (codec, name, type, priority) in nvdecCodecs)
            {
                if (ffmpegDecoders.Contains(codec))
                {
                    var decoder = new HardwareDecoder
                    {
                        Name = name,
                        Type = type,
                        FfmpegCodec = codec,
                        Priority = priority,
                        MaxResolution = "8192x8192",
                        SupportsDeinterlace = true,
                        SupportsScaling = true
                    };

                    if (await TestDecoderAsync(decoder, ct))
                    {
                        lock (_lock) _decoders.Add(decoder);
                    }
                }
            }
        }

        private async Task DetectQsvDecodeAsync(HashSet<string> ffmpegDecoders, CancellationToken ct)
        {
            var qsvCodecs = new[]
            {
                ("h264_qsv", "Intel QuickSync H.264", HardwareDecoderType.QsvH264, 80),
                ("hevc_qsv", "Intel QuickSync HEVC", HardwareDecoderType.QsvHevc, 75),
                ("av1_qsv", "Intel QuickSync AV1", HardwareDecoderType.QsvAv1, 70),
                ("vp9_qsv", "Intel QuickSync VP9", HardwareDecoderType.QsvVp9, 65),
                ("mpeg2_qsv", "Intel QuickSync MPEG-2", HardwareDecoderType.QsvMpeg2, 55)
            };

            foreach (var (codec, name, type, priority) in qsvCodecs)
            {
                if (ffmpegDecoders.Contains(codec))
                {
                    var decoder = new HardwareDecoder
                    {
                        Name = name,
                        Type = type,
                        FfmpegCodec = codec,
                        Priority = priority,
                        MaxResolution = "8192x8192",
                        SupportsDeinterlace = true,
                        SupportsScaling = true
                    };

                    if (await TestDecoderAsync(decoder, ct))
                    {
                        lock (_lock) _decoders.Add(decoder);
                    }
                }
            }
        }

        private async Task DetectD3D11VAAsync(HashSet<string> ffmpegDecoders, CancellationToken ct)
        {
            // D3D11VA generic decoders
            var d3d11Codecs = new[]
            {
                ("h264", "D3D11VA H.264", HardwareDecoderType.D3D11VA_H264, 60),
                ("hevc", "D3D11VA HEVC", HardwareDecoderType.D3D11VA_Hevc, 55),
                ("vp9", "D3D11VA VP9", HardwareDecoderType.D3D11VA_Vp9, 50)
            };

            foreach (var (codec, name, type, priority) in d3d11Codecs)
            {
                // D3D11VA hwaccel ile test et
                var decoder = new HardwareDecoder
                {
                    Name = name,
                    Type = type,
                    FfmpegCodec = codec,
                    FfmpegHwaccel = "d3d11va",
                    Priority = priority,
                    MaxResolution = "4096x4096"
                };

                if (await TestDecoderWithHwaccelAsync(decoder, ct))
                {
                    lock (_lock) _decoders.Add(decoder);
                }
            }
        }

        private async Task DetectDXVA2Async(HashSet<string> ffmpegDecoders, CancellationToken ct)
        {
            // DXVA2 (eski GPU'lar için fallback)
            var decoder = new HardwareDecoder
            {
                Name = "DXVA2 H.264",
                Type = HardwareDecoderType.DXVA2_H264,
                FfmpegCodec = "h264",
                FfmpegHwaccel = "dxva2",
                Priority = 40,
                MaxResolution = "4096x4096"
            };

            if (await TestDecoderWithHwaccelAsync(decoder, ct))
            {
                lock (_lock) _decoders.Add(decoder);
            }
        }

        #endregion

        #region FFmpeg Integration

        /// <summary>
        /// Decoder için FFmpeg parametrelerini oluştur
        /// </summary>
        public string GetDecoderArgs(HardwareDecoder decoder, string inputFile)
        {
            var args = new List<string>();

            // Hardware acceleration
            if (!string.IsNullOrEmpty(decoder.FfmpegHwaccel))
            {
                args.Add($"-hwaccel {decoder.FfmpegHwaccel}");
                args.Add("-hwaccel_output_format nv12"); // Universal format
            }

            // Codec
            if (decoder.FfmpegCodec.Contains("cuvid") || decoder.FfmpegCodec.Contains("qsv"))
            {
                args.Add($"-c:v {decoder.FfmpegCodec}");
            }

            // Input
            args.Add($"-i \"{inputFile}\"");

            return string.Join(" ", args);
        }

        /// <summary>
        /// Video dosyasını decode et ve frame'lere ayır
        /// </summary>
        public async Task<bool> DecodeToFramesAsync(
            string inputFile,
            string outputPattern,
            HardwareDecoder? decoder = null,
            int? maxFrames = null,
            CancellationToken ct = default)
        {
            decoder ??= BestDecoder;
            if (decoder == null)
            {
                Debug.WriteLine("[HardwareDecoder] No decoder available, using software");
            }

            try
            {
                var ffmpegPath = FfmpegProcess.ResolveFfmpegPath();
                var args = new List<string>();

                // Hardware decoder args
                if (decoder != null)
                {
                    if (!string.IsNullOrEmpty(decoder.FfmpegHwaccel))
                    {
                        args.Add($"-hwaccel {decoder.FfmpegHwaccel}");
                    }
                    if (decoder.FfmpegCodec.Contains("cuvid") || decoder.FfmpegCodec.Contains("qsv"))
                    {
                        args.Add($"-c:v {decoder.FfmpegCodec}");
                    }
                }

                args.Add($"-i \"{inputFile}\"");

                if (maxFrames.HasValue)
                {
                    args.Add($"-frames:v {maxFrames.Value}");
                }

                args.Add($"-f image2 \"{outputPattern}\"");

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = string.Join(" ", args),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    }
                };

                process.Start();
                await process.WaitForExitAsync(ct);

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HardwareDecoder] DecodeToFrames error: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Helper Methods

        private async Task<HashSet<string>> GetFfmpegDecodersAsync(string? ffmpegPath, CancellationToken ct)
        {
            var decoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var path = ffmpegPath ?? FfmpegProcess.ResolveFfmpegPath();

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = "-decoders",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    }
                };

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                // V..... h264_cuvid           Nvidia CUVID H264 decoder (codec h264)
                var regex = new Regex(@"V\.{5}\s+(\w+)\s+", RegexOptions.Multiline);
                foreach (Match match in regex.Matches(output))
                {
                    decoders.Add(match.Groups[1].Value);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HardwareDecoder] FFmpeg decoder list error: {ex.Message}");
            }

            return decoders;
        }

        private async Task<GpuVendor> DetectGpuVendorAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var searcher = new System.Management.ManagementObjectSearcher(
                        "SELECT Name FROM Win32_VideoController");

                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        var name = obj["Name"]?.ToString()?.ToLowerInvariant() ?? "";
                        
                        if (name.Contains("nvidia"))
                            return GpuVendor.Nvidia;
                        if (name.Contains("amd") || name.Contains("radeon"))
                            return GpuVendor.Amd;
                        if (name.Contains("intel"))
                            return GpuVendor.Intel;
                    }
                }
                catch { }

                return GpuVendor.Unknown;
            });
        }

        private bool HasIntelIntegrated()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name FROM Win32_VideoController");

                foreach (System.Management.ManagementObject obj in searcher.Get())
                {
                    var name = obj["Name"]?.ToString()?.ToLowerInvariant() ?? "";
                    if (name.Contains("intel"))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private async Task<bool> TestDecoderAsync(HardwareDecoder decoder, CancellationToken ct)
        {
            try
            {
                var args = $"-f lavfi -i testsrc=duration=0.1:size=320x240:rate=30 " +
                          $"-c:v {decoder.FfmpegCodec} -frames:v 1 -f null -";

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = FfmpegProcess.ResolveFfmpegPath(),
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    }
                };

                process.Start();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                await process.WaitForExitAsync(cts.Token);

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HardwareDecoder] Test failed for {decoder.Name}: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> TestDecoderWithHwaccelAsync(HardwareDecoder decoder, CancellationToken ct)
        {
            try
            {
                // Test video ile hwaccel test et
                var args = $"-hwaccel {decoder.FfmpegHwaccel} " +
                          $"-f lavfi -i testsrc=duration=0.1:size=320x240:rate=30 " +
                          $"-frames:v 1 -f null -";

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = FfmpegProcess.ResolveFfmpegPath(),
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    }
                };

                process.Start();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                await process.WaitForExitAsync(cts.Token);

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HardwareDecoder] Hwaccel test failed for {decoder.Name}: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lock)
            {
                _decoders.Clear();
            }
        }

        #endregion
    }

    #region Supporting Types

    public enum HardwareDecoderType
    {
        Software,
        // NVIDIA
        NvdecH264,
        NvdecHevc,
        NvdecAv1,
        NvdecVp9,
        NvdecMpeg2,
        NvdecMpeg4,
        // Intel
        QsvH264,
        QsvHevc,
        QsvAv1,
        QsvVp9,
        QsvMpeg2,
        // D3D11VA
        D3D11VA_H264,
        D3D11VA_Hevc,
        D3D11VA_Vp9,
        // DXVA2
        DXVA2_H264
    }

    public class HardwareDecoder
    {
        public string Name { get; set; } = "";
        public HardwareDecoderType Type { get; set; }
        public string FfmpegCodec { get; set; } = "";
        public string FfmpegHwaccel { get; set; } = "";
        public int Priority { get; set; }
        public string MaxResolution { get; set; } = "";
        public bool SupportsDeinterlace { get; set; }
        public bool SupportsScaling { get; set; }
    }

    #endregion
}
