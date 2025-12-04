using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UniCast.Encoder.Hardware
{
    /// <summary>
    /// Hardware encoder detection ve yönetimi.
    /// NVENC (NVIDIA), QSV (Intel), AMF (AMD) desteği.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class HardwareEncoderService : IDisposable
    {
        #region Singleton

        private static readonly Lazy<HardwareEncoderService> _instance =
            new(() => new HardwareEncoderService(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static HardwareEncoderService Instance => _instance.Value;

        #endregion

        #region Properties

        /// <summary>
        /// Tespit edilen hardware encoder'lar
        /// </summary>
        public IReadOnlyList<HardwareEncoder> AvailableEncoders => _encoders.AsReadOnly();

        /// <summary>
        /// En iyi encoder (performans sırasına göre)
        /// </summary>
        public HardwareEncoder? BestEncoder => _encoders.FirstOrDefault();

        /// <summary>
        /// Hardware encoding destekleniyor mu?
        /// </summary>
        public bool IsHardwareEncodingAvailable => _encoders.Count > 0;

        /// <summary>
        /// Detection tamamlandı mı?
        /// </summary>
        public bool IsDetectionComplete { get; private set; }

        #endregion

        #region Fields

        private readonly List<HardwareEncoder> _encoders = new();
        private readonly object _lock = new();
        private GpuInfo? _gpuInfo;
        private bool _disposed;

        #endregion

        #region Constructor

        private HardwareEncoderService()
        {
            // Detection asenkron yapılacak
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Hardware encoder'ları tespit et
        /// </summary>
        public async Task<IReadOnlyList<HardwareEncoder>> DetectEncodersAsync(
            string? ffmpegPath = null,
            CancellationToken ct = default)
        {
            if (IsDetectionComplete)
                return _encoders.AsReadOnly();

            lock (_lock)
            {
                if (IsDetectionComplete)
                    return _encoders.AsReadOnly();

                _encoders.Clear();
            }

            try
            {
                // 1. GPU bilgisini al
                _gpuInfo = await DetectGpuAsync();
                Debug.WriteLine($"[HardwareEncoder] GPU: {_gpuInfo?.Name ?? "Unknown"}");

                // 2. FFmpeg'den encoder listesini al
                var ffmpegEncoders = await GetFfmpegEncodersAsync(ffmpegPath, ct);

                // 3. NVIDIA NVENC
                if (_gpuInfo?.Vendor == GpuVendor.Nvidia)
                {
                    await DetectNvencAsync(ffmpegEncoders, ct);
                }

                // 4. Intel QuickSync
                if (_gpuInfo?.Vendor == GpuVendor.Intel || HasIntelIntegrated())
                {
                    await DetectQsvAsync(ffmpegEncoders, ct);
                }

                // 5. AMD AMF/VCE
                if (_gpuInfo?.Vendor == GpuVendor.Amd)
                {
                    await DetectAmfAsync(ffmpegEncoders, ct);
                }

                // 6. Performans sırasına göre sırala
                lock (_lock)
                {
                    _encoders.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                }

                IsDetectionComplete = true;

                Debug.WriteLine($"[HardwareEncoder] Tespit edilen encoder sayısı: {_encoders.Count}");
                foreach (var enc in _encoders)
                {
                    Debug.WriteLine($"  - {enc.Name} ({enc.Type}) Priority: {enc.Priority}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HardwareEncoder] Detection hatası: {ex.Message}");
                IsDetectionComplete = true;
            }

            return _encoders.AsReadOnly();
        }

        /// <summary>
        /// Belirli encoder için FFmpeg parametrelerini al
        /// </summary>
        public EncoderParameters GetEncoderParameters(
            HardwareEncoderType type,
            EncoderPreset preset = EncoderPreset.Balanced,
            int bitrate = 6000,
            int fps = 30)
        {
            return type switch
            {
                HardwareEncoderType.NvencH264 => GetNvencH264Params(preset, bitrate, fps),
                HardwareEncoderType.NvencHevc => GetNvencHevcParams(preset, bitrate, fps),
                HardwareEncoderType.NvencAv1 => GetNvencAv1Params(preset, bitrate, fps),
                HardwareEncoderType.QsvH264 => GetQsvH264Params(preset, bitrate, fps),
                HardwareEncoderType.QsvHevc => GetQsvHevcParams(preset, bitrate, fps),
                HardwareEncoderType.QsvAv1 => GetQsvAv1Params(preset, bitrate, fps),
                HardwareEncoderType.AmfH264 => GetAmfH264Params(preset, bitrate, fps),
                HardwareEncoderType.AmfHevc => GetAmfHevcParams(preset, bitrate, fps),
                HardwareEncoderType.AmfAv1 => GetAmfAv1Params(preset, bitrate, fps),
                _ => GetSoftwareParams(preset, bitrate, fps)
            };
        }

        /// <summary>
        /// Encoder'ı benchmark yap
        /// </summary>
        public async Task<EncoderBenchmarkResult> BenchmarkEncoderAsync(
            HardwareEncoder encoder,
            int durationSeconds = 5,
            CancellationToken ct = default)
        {
            var result = new EncoderBenchmarkResult { Encoder = encoder };
            var sw = Stopwatch.StartNew();

            try
            {
                var testArgs = BuildBenchmarkArgs(encoder, durationSeconds);

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = FfmpegProcess.ResolveFfmpegPath(),
                        Arguments = testArgs,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    }
                };

                var frameCount = 0;
                var totalBitrate = 0.0;
                var samples = 0;

                process.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;

                    // frame= 150 fps= 60 q=23.0 size= 1024kB time=00:00:05.00 bitrate=1677.7kbits/s
                    var fpsMatch = Regex.Match(e.Data, @"fps=\s*([\d.]+)");
                    if (fpsMatch.Success && double.TryParse(fpsMatch.Groups[1].Value, out var fps))
                    {
                        result.AverageFps = (result.AverageFps * samples + fps) / (samples + 1);
                        samples++;
                    }

                    var frameMatch = Regex.Match(e.Data, @"frame=\s*(\d+)");
                    if (frameMatch.Success)
                    {
                        frameCount = int.Parse(frameMatch.Groups[1].Value);
                    }

                    var bitrateMatch = Regex.Match(e.Data, @"bitrate=\s*([\d.]+)");
                    if (bitrateMatch.Success && double.TryParse(bitrateMatch.Groups[1].Value, out var br))
                    {
                        totalBitrate += br;
                    }
                };

                process.Start();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(ct);

                sw.Stop();

                result.Success = process.ExitCode == 0;
                result.EncodingTimeMs = sw.ElapsedMilliseconds;
                result.FramesEncoded = frameCount;
                result.CpuUsagePercent = await GetAverageCpuUsageAsync();

                // Performans skoru hesapla (FPS * 100 / CPU Usage)
                if (result.CpuUsagePercent > 0)
                {
                    result.PerformanceScore = (int)(result.AverageFps * 100 / result.CpuUsagePercent);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        #endregion

        #region NVENC Detection & Parameters

        private async Task DetectNvencAsync(HashSet<string> ffmpegEncoders, CancellationToken ct)
        {
            // NVENC H.264
            if (ffmpegEncoders.Contains("h264_nvenc"))
            {
                var encoder = new HardwareEncoder
                {
                    Name = "NVIDIA NVENC H.264",
                    Type = HardwareEncoderType.NvencH264,
                    FfmpegCodec = "h264_nvenc",
                    Priority = 100,
                    MaxBitrate = 100000, // 100 Mbps
                    MaxResolution = "4096x4096",
                    SupportsLookahead = true,
                    SupportsBFrames = true
                };

                // Gerçekten çalışıyor mu test et
                if (await TestEncoderAsync(encoder, ct))
                {
                    lock (_lock) _encoders.Add(encoder);
                }
            }

            // NVENC HEVC
            if (ffmpegEncoders.Contains("hevc_nvenc"))
            {
                var encoder = new HardwareEncoder
                {
                    Name = "NVIDIA NVENC HEVC",
                    Type = HardwareEncoderType.NvencHevc,
                    FfmpegCodec = "hevc_nvenc",
                    Priority = 95,
                    MaxBitrate = 100000,
                    MaxResolution = "8192x8192",
                    SupportsLookahead = true,
                    SupportsBFrames = true
                };

                if (await TestEncoderAsync(encoder, ct))
                {
                    lock (_lock) _encoders.Add(encoder);
                }
            }

            // NVENC AV1 (RTX 40 serisi)
            if (ffmpegEncoders.Contains("av1_nvenc"))
            {
                var encoder = new HardwareEncoder
                {
                    Name = "NVIDIA NVENC AV1",
                    Type = HardwareEncoderType.NvencAv1,
                    FfmpegCodec = "av1_nvenc",
                    Priority = 90,
                    MaxBitrate = 100000,
                    MaxResolution = "8192x8192",
                    SupportsLookahead = true,
                    SupportsBFrames = false
                };

                if (await TestEncoderAsync(encoder, ct))
                {
                    lock (_lock) _encoders.Add(encoder);
                }
            }
        }

        private EncoderParameters GetNvencH264Params(EncoderPreset preset, int bitrate, int fps)
        {
            var p = new EncoderParameters
            {
                Codec = "h264_nvenc",
                Bitrate = bitrate,
                MaxBitrate = (int)(bitrate * 1.5),
                BufferSize = bitrate * 2,
                Fps = fps,
                KeyframeInterval = fps * 2 // 2 saniye
            };

            // Preset mapping
            p.Preset = preset switch
            {
                EncoderPreset.Quality => "p7",      // Slowest, best quality
                EncoderPreset.Balanced => "p4",     // Medium
                EncoderPreset.Performance => "p1",  // Fastest
                EncoderPreset.LowLatency => "p1",
                _ => "p4"
            };

            // Tune
            p.Tune = preset == EncoderPreset.LowLatency ? "ll" : "hq";

            // Rate control
            p.RateControl = "cbr"; // Streaming için CBR

            // Extra params
            p.ExtraParams = new Dictionary<string, string>
            {
                ["rc-lookahead"] = preset == EncoderPreset.LowLatency ? "0" : "32",
                ["spatial-aq"] = "1",
                ["temporal-aq"] = "1",
                ["zerolatency"] = preset == EncoderPreset.LowLatency ? "1" : "0",
                ["b_ref_mode"] = "middle"
            };

            return p;
        }

        private EncoderParameters GetNvencHevcParams(EncoderPreset preset, int bitrate, int fps)
        {
            var p = GetNvencH264Params(preset, bitrate, fps);
            p.Codec = "hevc_nvenc";
            p.Profile = "main";
            return p;
        }

        private EncoderParameters GetNvencAv1Params(EncoderPreset preset, int bitrate, int fps)
        {
            var p = GetNvencH264Params(preset, bitrate, fps);
            p.Codec = "av1_nvenc";
            p.ExtraParams.Remove("b_ref_mode"); // AV1'de yok
            return p;
        }

        #endregion

        #region QSV Detection & Parameters

        private async Task DetectQsvAsync(HashSet<string> ffmpegEncoders, CancellationToken ct)
        {
            // QSV H.264
            if (ffmpegEncoders.Contains("h264_qsv"))
            {
                var encoder = new HardwareEncoder
                {
                    Name = "Intel QuickSync H.264",
                    Type = HardwareEncoderType.QsvH264,
                    FfmpegCodec = "h264_qsv",
                    Priority = 80,
                    MaxBitrate = 50000,
                    MaxResolution = "4096x4096",
                    SupportsLookahead = true,
                    SupportsBFrames = true
                };

                if (await TestEncoderAsync(encoder, ct))
                {
                    lock (_lock) _encoders.Add(encoder);
                }
            }

            // QSV HEVC
            if (ffmpegEncoders.Contains("hevc_qsv"))
            {
                var encoder = new HardwareEncoder
                {
                    Name = "Intel QuickSync HEVC",
                    Type = HardwareEncoderType.QsvHevc,
                    FfmpegCodec = "hevc_qsv",
                    Priority = 75,
                    MaxBitrate = 50000,
                    MaxResolution = "8192x8192"
                };

                if (await TestEncoderAsync(encoder, ct))
                {
                    lock (_lock) _encoders.Add(encoder);
                }
            }

            // QSV AV1 (Arc GPU veya yeni Intel)
            if (ffmpegEncoders.Contains("av1_qsv"))
            {
                var encoder = new HardwareEncoder
                {
                    Name = "Intel QuickSync AV1",
                    Type = HardwareEncoderType.QsvAv1,
                    FfmpegCodec = "av1_qsv",
                    Priority = 70,
                    MaxBitrate = 50000,
                    MaxResolution = "8192x8192"
                };

                if (await TestEncoderAsync(encoder, ct))
                {
                    lock (_lock) _encoders.Add(encoder);
                }
            }
        }

        private EncoderParameters GetQsvH264Params(EncoderPreset preset, int bitrate, int fps)
        {
            var p = new EncoderParameters
            {
                Codec = "h264_qsv",
                Bitrate = bitrate,
                MaxBitrate = (int)(bitrate * 1.5),
                BufferSize = bitrate * 2,
                Fps = fps,
                KeyframeInterval = fps * 2,
                RateControl = "cbr"
            };

            p.Preset = preset switch
            {
                EncoderPreset.Quality => "veryslow",
                EncoderPreset.Balanced => "medium",
                EncoderPreset.Performance => "veryfast",
                EncoderPreset.LowLatency => "veryfast",
                _ => "medium"
            };

            p.ExtraParams = new Dictionary<string, string>
            {
                ["look_ahead"] = preset == EncoderPreset.LowLatency ? "0" : "1",
                ["look_ahead_depth"] = "40",
                ["global_quality"] = "25"
            };

            return p;
        }

        private EncoderParameters GetQsvHevcParams(EncoderPreset preset, int bitrate, int fps)
        {
            var p = GetQsvH264Params(preset, bitrate, fps);
            p.Codec = "hevc_qsv";
            return p;
        }

        private EncoderParameters GetQsvAv1Params(EncoderPreset preset, int bitrate, int fps)
        {
            var p = GetQsvH264Params(preset, bitrate, fps);
            p.Codec = "av1_qsv";
            return p;
        }

        #endregion

        #region AMF Detection & Parameters

        private async Task DetectAmfAsync(HashSet<string> ffmpegEncoders, CancellationToken ct)
        {
            // AMF H.264
            if (ffmpegEncoders.Contains("h264_amf"))
            {
                var encoder = new HardwareEncoder
                {
                    Name = "AMD AMF H.264",
                    Type = HardwareEncoderType.AmfH264,
                    FfmpegCodec = "h264_amf",
                    Priority = 70,
                    MaxBitrate = 50000,
                    MaxResolution = "4096x4096"
                };

                if (await TestEncoderAsync(encoder, ct))
                {
                    lock (_lock) _encoders.Add(encoder);
                }
            }

            // AMF HEVC
            if (ffmpegEncoders.Contains("hevc_amf"))
            {
                var encoder = new HardwareEncoder
                {
                    Name = "AMD AMF HEVC",
                    Type = HardwareEncoderType.AmfHevc,
                    FfmpegCodec = "hevc_amf",
                    Priority = 65,
                    MaxBitrate = 50000,
                    MaxResolution = "8192x8192"
                };

                if (await TestEncoderAsync(encoder, ct))
                {
                    lock (_lock) _encoders.Add(encoder);
                }
            }

            // AMF AV1 (RX 7000 serisi)
            if (ffmpegEncoders.Contains("av1_amf"))
            {
                var encoder = new HardwareEncoder
                {
                    Name = "AMD AMF AV1",
                    Type = HardwareEncoderType.AmfAv1,
                    FfmpegCodec = "av1_amf",
                    Priority = 60,
                    MaxBitrate = 50000,
                    MaxResolution = "8192x8192"
                };

                if (await TestEncoderAsync(encoder, ct))
                {
                    lock (_lock) _encoders.Add(encoder);
                }
            }
        }

        private EncoderParameters GetAmfH264Params(EncoderPreset preset, int bitrate, int fps)
        {
            var p = new EncoderParameters
            {
                Codec = "h264_amf",
                Bitrate = bitrate,
                MaxBitrate = (int)(bitrate * 1.5),
                BufferSize = bitrate * 2,
                Fps = fps,
                KeyframeInterval = fps * 2,
                RateControl = "cbr"
            };

            p.ExtraParams = new Dictionary<string, string>
            {
                ["usage"] = preset == EncoderPreset.LowLatency ? "ultralowlatency" : "transcoding",
                ["quality"] = preset switch
                {
                    EncoderPreset.Quality => "quality",
                    EncoderPreset.Balanced => "balanced",
                    _ => "speed"
                },
                ["rc"] = "cbr"
            };

            return p;
        }

        private EncoderParameters GetAmfHevcParams(EncoderPreset preset, int bitrate, int fps)
        {
            var p = GetAmfH264Params(preset, bitrate, fps);
            p.Codec = "hevc_amf";
            return p;
        }

        private EncoderParameters GetAmfAv1Params(EncoderPreset preset, int bitrate, int fps)
        {
            var p = GetAmfH264Params(preset, bitrate, fps);
            p.Codec = "av1_amf";
            return p;
        }

        #endregion

        #region Software Fallback

        private EncoderParameters GetSoftwareParams(EncoderPreset preset, int bitrate, int fps)
        {
            var p = new EncoderParameters
            {
                Codec = "libx264",
                Bitrate = bitrate,
                MaxBitrate = (int)(bitrate * 1.5),
                BufferSize = bitrate * 2,
                Fps = fps,
                KeyframeInterval = fps * 2,
                RateControl = "cbr"
            };

            p.Preset = preset switch
            {
                EncoderPreset.Quality => "slow",
                EncoderPreset.Balanced => "veryfast",
                EncoderPreset.Performance => "ultrafast",
                EncoderPreset.LowLatency => "ultrafast",
                _ => "veryfast"
            };

            p.Tune = preset == EncoderPreset.LowLatency ? "zerolatency" : "film";

            return p;
        }

        #endregion

        #region Helper Methods

        private async Task<GpuInfo?> DetectGpuAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController");

                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var name = obj["Name"]?.ToString() ?? "";
                        var vendor = name.ToLowerInvariant() switch
                        {
                            var n when n.Contains("nvidia") => GpuVendor.Nvidia,
                            var n when n.Contains("amd") || n.Contains("radeon") => GpuVendor.Amd,
                            var n when n.Contains("intel") => GpuVendor.Intel,
                            _ => GpuVendor.Unknown
                        };

                        // İlk dedicated GPU'yu al
                        if (vendor != GpuVendor.Unknown && vendor != GpuVendor.Intel)
                        {
                            return new GpuInfo
                            {
                                Name = name,
                                Vendor = vendor,
                                VramBytes = Convert.ToInt64(obj["AdapterRAM"] ?? 0),
                                DriverVersion = obj["DriverVersion"]?.ToString() ?? ""
                            };
                        }
                    }

                    // Dedicated bulunamadıysa Intel integrated
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var name = obj["Name"]?.ToString() ?? "";
                        if (name.ToLowerInvariant().Contains("intel"))
                        {
                            return new GpuInfo
                            {
                                Name = name,
                                Vendor = GpuVendor.Intel,
                                VramBytes = Convert.ToInt64(obj["AdapterRAM"] ?? 0),
                                DriverVersion = obj["DriverVersion"]?.ToString() ?? ""
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HardwareEncoder] GPU detection error: {ex.Message}");
                }

                return null;
            });
        }

        private bool HasIntelIntegrated()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_VideoController");

                foreach (ManagementObject obj in searcher.Get())
                {
                    var name = obj["Name"]?.ToString()?.ToLowerInvariant() ?? "";
                    if (name.Contains("intel"))
                        return true;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[HardwareEncoder] Intel iGPU detection hatası: {ex.Message}"); }

            return false;
        }

        private async Task<HashSet<string>> GetFfmpegEncodersAsync(string? ffmpegPath, CancellationToken ct)
        {
            var encoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var path = ffmpegPath ?? FfmpegProcess.ResolveFfmpegPath();

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = "-encoders",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    }
                };

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                // V..... h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
                var regex = new Regex(@"V\.{5}\s+(\w+)\s+", RegexOptions.Multiline);
                foreach (Match match in regex.Matches(output))
                {
                    encoders.Add(match.Groups[1].Value);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HardwareEncoder] FFmpeg encoder list error: {ex.Message}");
            }

            return encoders;
        }

        private async Task<bool> TestEncoderAsync(HardwareEncoder encoder, CancellationToken ct)
        {
            try
            {
                // Kısa bir test encoding yap
                var args = $"-f lavfi -i testsrc=duration=0.5:size=320x240:rate=30 " +
                          $"-c:v {encoder.FfmpegCodec} -frames:v 10 -f null -";

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
                Debug.WriteLine($"[HardwareEncoder] Test failed for {encoder.Name}: {ex.Message}");
                return false;
            }
        }

        private string BuildBenchmarkArgs(HardwareEncoder encoder, int durationSeconds)
        {
            return $"-f lavfi -i testsrc=duration={durationSeconds}:size=1920x1080:rate=60 " +
                   $"-c:v {encoder.FfmpegCodec} -b:v 6000k -f null -";
        }

        private async Task<double> GetAverageCpuUsageAsync()
        {
            // Basit CPU kullanımı ölçümü
            var startTime = DateTime.UtcNow;
            var startCpuUsage = Process.GetCurrentProcess().TotalProcessorTime;

            await Task.Delay(100);

            var endTime = DateTime.UtcNow;
            var endCpuUsage = Process.GetCurrentProcess().TotalProcessorTime;

            var cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
            var totalMsPassed = (endTime - startTime).TotalMilliseconds;
            var cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);

            return cpuUsageTotal * 100;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lock)
            {
                _encoders.Clear();
            }
        }

        #endregion
    }

    #region Supporting Types

    public enum GpuVendor
    {
        Unknown,
        Nvidia,
        Amd,
        Intel
    }

    public enum HardwareEncoderType
    {
        Software,
        NvencH264,
        NvencHevc,
        NvencAv1,
        QsvH264,
        QsvHevc,
        QsvAv1,
        AmfH264,
        AmfHevc,
        AmfAv1
    }

    public enum EncoderPreset
    {
        Quality,      // En iyi kalite, yüksek CPU/GPU
        Balanced,     // Dengeli
        Performance,  // En hızlı
        LowLatency    // Düşük gecikme (streaming için)
    }

    public class GpuInfo
    {
        public string Name { get; set; } = "";
        public GpuVendor Vendor { get; set; }
        public long VramBytes { get; set; }
        public string DriverVersion { get; set; } = "";

        public double VramGb => VramBytes / (1024.0 * 1024.0 * 1024.0);
    }

    public class HardwareEncoder
    {
        public string Name { get; set; } = "";
        public HardwareEncoderType Type { get; set; }
        public string FfmpegCodec { get; set; } = "";
        public int Priority { get; set; }
        public int MaxBitrate { get; set; }
        public string MaxResolution { get; set; } = "";
        public bool SupportsLookahead { get; set; }
        public bool SupportsBFrames { get; set; }
    }

    public class EncoderParameters
    {
        public string Codec { get; set; } = "";
        public string Preset { get; set; } = "";
        public string Tune { get; set; } = "";
        public string Profile { get; set; } = "main";
        public string RateControl { get; set; } = "cbr";
        public int Bitrate { get; set; }
        public int MaxBitrate { get; set; }
        public int BufferSize { get; set; }
        public int Fps { get; set; }
        public int KeyframeInterval { get; set; }
        public Dictionary<string, string> ExtraParams { get; set; } = new();

        /// <summary>
        /// FFmpeg video encoding argümanlarını oluştur
        /// </summary>
        public string ToFfmpegArgs()
        {
            var sb = new System.Text.StringBuilder();

            sb.Append($"-c:v {Codec} ");

            if (!string.IsNullOrEmpty(Preset))
                sb.Append($"-preset {Preset} ");

            if (!string.IsNullOrEmpty(Tune))
                sb.Append($"-tune {Tune} ");

            if (!string.IsNullOrEmpty(Profile))
                sb.Append($"-profile:v {Profile} ");

            sb.Append($"-b:v {Bitrate}k ");
            sb.Append($"-maxrate {MaxBitrate}k ");
            sb.Append($"-bufsize {BufferSize}k ");
            sb.Append($"-r {Fps} ");
            sb.Append($"-g {KeyframeInterval} ");

            foreach (var (key, value) in ExtraParams)
            {
                sb.Append($"-{key} {value} ");
            }

            return sb.ToString().Trim();
        }
    }

    public class EncoderBenchmarkResult
    {
        public HardwareEncoder Encoder { get; set; } = null!;
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public long EncodingTimeMs { get; set; }
        public int FramesEncoded { get; set; }
        public double AverageFps { get; set; }
        public double CpuUsagePercent { get; set; }
        public int PerformanceScore { get; set; }
    }

    #endregion
}
