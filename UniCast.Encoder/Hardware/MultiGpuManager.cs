using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Encoder.Memory;

namespace UniCast.Encoder.Hardware
{
    /// <summary>
    /// Multi-GPU management ve load balancing.
    /// 
    /// Özellikler:
    /// - Birden fazla GPU detection
    /// - Load balancing (round-robin, least-loaded)
    /// - Paralel encoding (farklı çıkışlar için)
    /// - GPU affinity (encoder-to-GPU mapping)
    /// - Failover (GPU hata durumunda geçiş)
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class MultiGpuManager : IDisposable
    {
        #region Singleton

        private static readonly Lazy<MultiGpuManager> _instance =
            new(() => new MultiGpuManager(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static MultiGpuManager Instance => _instance.Value;

        #endregion

        #region Properties

        /// <summary>
        /// Tespit edilen tüm GPU'lar
        /// </summary>
        public IReadOnlyList<GpuDevice> Devices => _devices.AsReadOnly();

        /// <summary>
        /// Encoding destekleyen GPU'lar
        /// </summary>
        public IReadOnlyList<GpuDevice> EncodingCapableDevices =>
            _devices.Where(d => d.SupportsEncoding).ToList().AsReadOnly();

        /// <summary>
        /// Toplam GPU sayısı
        /// </summary>
        public int DeviceCount => _devices.Count;

        /// <summary>
        /// Multi-GPU modu aktif mi?
        /// </summary>
        public bool IsMultiGpuEnabled => EncodingCapableDevices.Count > 1;

        /// <summary>
        /// Detection tamamlandı mı?
        /// </summary>
        public bool IsDetectionComplete { get; private set; }

        /// <summary>
        /// Aktif load balancing stratejisi
        /// </summary>
        public LoadBalancingStrategy LoadBalancing { get; set; } = LoadBalancingStrategy.LeastLoaded;

        #endregion

        #region Fields

        private readonly List<GpuDevice> _devices = new();
        private readonly Dictionary<int, GpuWorkload> _workloads = new();
        private readonly object _lock = new();
        private int _roundRobinIndex;
        private bool _disposed;

        #endregion

        #region Constructor

        private MultiGpuManager() { }

        #endregion

        #region GPU Detection

        /// <summary>
        /// Tüm GPU'ları tespit et
        /// </summary>
        public async Task<IReadOnlyList<GpuDevice>> DetectGpusAsync(CancellationToken ct = default)
        {
            if (IsDetectionComplete)
                return _devices.AsReadOnly();

            lock (_lock)
            {
                if (IsDetectionComplete)
                    return _devices.AsReadOnly();

                _devices.Clear();
                _workloads.Clear();
            }

            try
            {
                // WMI ile GPU'ları tespit et
                await DetectGpusViaWmiAsync();

                // Her GPU için encoding capability kontrol et
                await DetectEncodingCapabilitiesAsync(ct);

                // Performans metrikleri başlat
                InitializeWorkloadTracking();

                IsDetectionComplete = true;

                Debug.WriteLine($"[MultiGpu] Tespit edilen GPU sayısı: {_devices.Count}");
                foreach (var gpu in _devices)
                {
                    Debug.WriteLine($"  [{gpu.Index}] {gpu.Name} ({gpu.Vendor})");
                    Debug.WriteLine($"      VRAM: {gpu.VramMB} MB, Encoding: {gpu.SupportsEncoding}");
                    if (gpu.SupportsEncoding)
                    {
                        Debug.WriteLine($"      Encoders: {string.Join(", ", gpu.SupportedEncoders)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiGpu] Detection error: {ex.Message}");
                IsDetectionComplete = true;
            }

            return _devices.AsReadOnly();
        }

        private async Task DetectGpusViaWmiAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    using var searcher = new System.Management.ManagementObjectSearcher(
                        "SELECT * FROM Win32_VideoController");

                    int index = 0;
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        var name = obj["Name"]?.ToString() ?? "Unknown GPU";
                        var vram = Convert.ToInt64(obj["AdapterRAM"] ?? 0);
                        var driverVersion = obj["DriverVersion"]?.ToString() ?? "";
                        var pnpId = obj["PNPDeviceID"]?.ToString() ?? "";

                        // Vendor tespit
                        var vendor = name.ToLowerInvariant() switch
                        {
                            var n when n.Contains("nvidia") => GpuVendor.Nvidia,
                            var n when n.Contains("amd") || n.Contains("radeon") => GpuVendor.Amd,
                            var n when n.Contains("intel") => GpuVendor.Intel,
                            _ => GpuVendor.Unknown
                        };

                        // GPU tipi tespit
                        var gpuType = pnpId.ToLowerInvariant() switch
                        {
                            var p when p.Contains("pci") => GpuType.Discrete,
                            _ => vendor == GpuVendor.Intel ? GpuType.Integrated : GpuType.Discrete
                        };

                        var device = new GpuDevice
                        {
                            Index = index++,
                            Name = name,
                            Vendor = vendor,
                            Type = gpuType,
                            VramBytes = vram,
                            DriverVersion = driverVersion,
                            PnpDeviceId = pnpId
                        };

                        lock (_lock)
                        {
                            _devices.Add(device);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MultiGpu] WMI error: {ex.Message}");
                }
            });
        }

        private async Task DetectEncodingCapabilitiesAsync(CancellationToken ct)
        {
            foreach (var device in _devices)
            {
                try
                {
                    var encoders = new List<string>();

                    // NVIDIA
                    if (device.Vendor == GpuVendor.Nvidia)
                    {
                        if (await TestEncoderAsync($"h264_nvenc", device.Index, ct))
                            encoders.Add("h264_nvenc");
                        if (await TestEncoderAsync($"hevc_nvenc", device.Index, ct))
                            encoders.Add("hevc_nvenc");
                        if (await TestEncoderAsync($"av1_nvenc", device.Index, ct))
                            encoders.Add("av1_nvenc");
                    }
                    // Intel
                    else if (device.Vendor == GpuVendor.Intel)
                    {
                        if (await TestEncoderAsync($"h264_qsv", device.Index, ct))
                            encoders.Add("h264_qsv");
                        if (await TestEncoderAsync($"hevc_qsv", device.Index, ct))
                            encoders.Add("hevc_qsv");
                        if (await TestEncoderAsync($"av1_qsv", device.Index, ct))
                            encoders.Add("av1_qsv");
                    }
                    // AMD
                    else if (device.Vendor == GpuVendor.Amd)
                    {
                        if (await TestEncoderAsync($"h264_amf", device.Index, ct))
                            encoders.Add("h264_amf");
                        if (await TestEncoderAsync($"hevc_amf", device.Index, ct))
                            encoders.Add("hevc_amf");
                        if (await TestEncoderAsync($"av1_amf", device.Index, ct))
                            encoders.Add("av1_amf");
                    }

                    device.SupportedEncoders = encoders;
                    device.SupportsEncoding = encoders.Count > 0;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MultiGpu] Encoding test error for GPU {device.Index}: {ex.Message}");
                }
            }
        }

        private async Task<bool> TestEncoderAsync(string encoder, int gpuIndex, CancellationToken ct)
        {
            try
            {
                // GPU index'i FFmpeg'e geçir
                var gpuArg = encoder.Contains("nvenc") ? $"-gpu {gpuIndex}" : "";

                var args = $"{gpuArg} -f lavfi -i testsrc=duration=0.1:size=320x240:rate=30 " +
                          $"-c:v {encoder} -frames:v 1 -f null -";

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
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                await process.WaitForExitAsync(cts.Token);

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private void InitializeWorkloadTracking()
        {
            foreach (var device in _devices)
            {
                _workloads[device.Index] = new GpuWorkload
                {
                    DeviceIndex = device.Index,
                    ActiveEncoders = 0,
                    EstimatedLoad = 0
                };
            }
        }

        #endregion

        #region Load Balancing

        /// <summary>
        /// Encoding için en uygun GPU'yu seç
        /// </summary>
        public GpuDevice? SelectGpuForEncoding(string? preferredEncoder = null)
        {
            var candidates = EncodingCapableDevices;

            if (candidates.Count == 0)
                return null;

            if (!string.IsNullOrEmpty(preferredEncoder))
            {
                candidates = candidates
                    .Where(d => d.SupportedEncoders.Contains(preferredEncoder))
                    .ToList();

                if (candidates.Count == 0)
                    return EncodingCapableDevices.FirstOrDefault();
            }

            return LoadBalancing switch
            {
                LoadBalancingStrategy.RoundRobin => SelectRoundRobin(candidates),
                LoadBalancingStrategy.LeastLoaded => SelectLeastLoaded(candidates),
                LoadBalancingStrategy.PreferDiscrete => SelectPreferDiscrete(candidates),
                LoadBalancingStrategy.PreferIntegrated => SelectPreferIntegrated(candidates),
                LoadBalancingStrategy.VramBased => SelectVramBased(candidates),
                _ => candidates.FirstOrDefault()
            };
        }

        private GpuDevice? SelectRoundRobin(IReadOnlyList<GpuDevice> candidates)
        {
            lock (_lock)
            {
                var device = candidates[_roundRobinIndex % candidates.Count];
                _roundRobinIndex++;
                return device;
            }
        }

        private GpuDevice? SelectLeastLoaded(IReadOnlyList<GpuDevice> candidates)
        {
            lock (_lock)
            {
                return candidates
                    .OrderBy(d => _workloads.TryGetValue(d.Index, out var w) ? w.EstimatedLoad : 0)
                    .ThenByDescending(d => d.VramMB)
                    .FirstOrDefault();
            }
        }

        private GpuDevice? SelectPreferDiscrete(IReadOnlyList<GpuDevice> candidates)
        {
            return candidates
                .OrderByDescending(d => d.Type == GpuType.Discrete ? 1 : 0)
                .ThenByDescending(d => d.VramMB)
                .FirstOrDefault();
        }

        private GpuDevice? SelectPreferIntegrated(IReadOnlyList<GpuDevice> candidates)
        {
            // İntegrated GPU'yu tercih et (discrete GPU oyun için serbest kalsın)
            return candidates
                .OrderByDescending(d => d.Type == GpuType.Integrated ? 1 : 0)
                .ThenByDescending(d => d.VramMB)
                .FirstOrDefault();
        }

        private GpuDevice? SelectVramBased(IReadOnlyList<GpuDevice> candidates)
        {
            return candidates
                .OrderByDescending(d => d.VramMB)
                .FirstOrDefault();
        }

        #endregion

        #region Workload Management

        /// <summary>
        /// GPU'da encoding session başlat
        /// </summary>
        public void BeginEncodingSession(int gpuIndex)
        {
            lock (_lock)
            {
                if (_workloads.TryGetValue(gpuIndex, out var workload))
                {
                    workload.ActiveEncoders++;
                    workload.EstimatedLoad = Math.Min(100, workload.ActiveEncoders * 25);
                    Debug.WriteLine($"[MultiGpu] GPU {gpuIndex}: Encoding started (Active: {workload.ActiveEncoders})");
                }
            }
        }

        /// <summary>
        /// GPU'daki encoding session'ı bitir
        /// </summary>
        public void EndEncodingSession(int gpuIndex)
        {
            lock (_lock)
            {
                if (_workloads.TryGetValue(gpuIndex, out var workload))
                {
                    workload.ActiveEncoders = Math.Max(0, workload.ActiveEncoders - 1);
                    workload.EstimatedLoad = workload.ActiveEncoders * 25;
                    Debug.WriteLine($"[MultiGpu] GPU {gpuIndex}: Encoding ended (Active: {workload.ActiveEncoders})");
                }
            }
        }

        /// <summary>
        /// GPU workload bilgisini al
        /// </summary>
        public GpuWorkload? GetWorkload(int gpuIndex)
        {
            lock (_lock)
            {
                return _workloads.TryGetValue(gpuIndex, out var workload) ? workload : null;
            }
        }

        /// <summary>
        /// Tüm GPU workload'larını al
        /// </summary>
        public IReadOnlyDictionary<int, GpuWorkload> GetAllWorkloads()
        {
            lock (_lock)
            {
                return new Dictionary<int, GpuWorkload>(_workloads);
            }
        }

        #endregion

        #region Parallel Encoding

        /// <summary>
        /// Birden fazla çıkış için paralel encoding (her biri farklı GPU'da)
        /// </summary>
        public async Task<ParallelEncodingResult> EncodeParallelAsync(
            string inputFile,
            IReadOnlyList<EncodingTask> tasks,
            CancellationToken ct = default)
        {
            var result = new ParallelEncodingResult();

            if (tasks.Count == 0)
                return result;

            // GPU assignment
            var gpuAssignments = new Dictionary<EncodingTask, GpuDevice>();
            foreach (var task in tasks)
            {
                var gpu = SelectGpuForEncoding(task.Encoder);
                if (gpu != null)
                {
                    gpuAssignments[task] = gpu;
                    BeginEncodingSession(gpu.Index);
                }
            }

            try
            {
                // Paralel encoding
                var encodingTasks = tasks.Select(async task =>
                {
                    if (!gpuAssignments.TryGetValue(task, out var gpu))
                    {
                        return new TaskResult { Task = task, Success = false, Error = "No GPU available" };
                    }

                    try
                    {
                        var success = await EncodeOnGpuAsync(inputFile, task, gpu, ct);
                        return new TaskResult { Task = task, Success = success, GpuUsed = gpu.Index };
                    }
                    catch (Exception ex)
                    {
                        return new TaskResult { Task = task, Success = false, Error = ex.Message };
                    }
                });

                var results = await Task.WhenAll(encodingTasks);
                result.TaskResults = results.ToList();
                result.SuccessCount = results.Count(r => r.Success);
                result.FailureCount = results.Count(r => !r.Success);
            }
            finally
            {
                // GPU sessions'ı temizle
                foreach (var gpu in gpuAssignments.Values)
                {
                    EndEncodingSession(gpu.Index);
                }
            }

            return result;
        }

        private async Task<bool> EncodeOnGpuAsync(
            string inputFile,
            EncodingTask task,
            GpuDevice gpu,
            CancellationToken ct)
        {
            var gpuArg = gpu.Vendor == GpuVendor.Nvidia ? $"-gpu {gpu.Index}" : "";

            var args = $"-i \"{inputFile}\" {gpuArg} -c:v {task.Encoder} " +
                      $"-b:v {task.Bitrate}k {task.ExtraArgs} \"{task.OutputFile}\"";

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
            await process.WaitForExitAsync(ct);

            return process.ExitCode == 0;
        }

        #endregion

        #region FFmpeg Integration

        /// <summary>
        /// Belirli GPU için FFmpeg argümanları oluştur
        /// </summary>
        public string GetGpuArgs(GpuDevice gpu, string encoder)
        {
            if (gpu.Vendor == GpuVendor.Nvidia)
            {
                return $"-gpu {gpu.Index}";
            }
            else if (gpu.Vendor == GpuVendor.Intel && encoder.Contains("qsv"))
            {
                // Intel için device path (opsiyonel)
                return $"-init_hw_device qsv=hw:{gpu.Index}";
            }
            else if (gpu.Vendor == GpuVendor.Amd)
            {
                // AMD için şu an multi-GPU FFmpeg'de sınırlı
                return "";
            }

            return "";
        }

        /// <summary>
        /// Birden fazla çıkış için FFmpeg tee muxer ile tek pass encoding
        /// </summary>
        public string BuildMultiOutputArgs(
            string input,
            IReadOnlyList<(GpuDevice Gpu, string Encoder, string Output, int Bitrate)> outputs)
        {
            if (outputs.Count == 0)
                return "";

            // Tek GPU kullanarak tee muxer ile birden fazla çıkış
            var gpu = outputs[0].Gpu;
            var encoder = outputs[0].Encoder;

            var args = $"-i \"{input}\" {GetGpuArgs(gpu, encoder)} -c:v {encoder} ";

            // Tee muxer
            var outputSpecs = outputs.Select(o =>
                $"[f=flv:b:v={o.Bitrate}k]{o.Output}");

            args += $"-f tee \"{string.Join("|", outputSpecs)}\"";

            return args;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lock)
            {
                _devices.Clear();
                _workloads.Clear();
            }
        }

        #endregion
    }

    #region Supporting Types

    public enum GpuType
    {
        Unknown,
        Integrated,  // iGPU (Intel, AMD APU)
        Discrete     // dGPU (NVIDIA, AMD)
    }

    public enum LoadBalancingStrategy
    {
        RoundRobin,       // Sırayla GPU seç
        LeastLoaded,      // En az yüklü GPU'yu seç
        PreferDiscrete,   // Discrete GPU'yu tercih et
        PreferIntegrated, // Integrated GPU'yu tercih et (discrete oyun için serbest)
        VramBased         // En fazla VRAM olan GPU'yu seç
    }

    public class GpuDevice
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public GpuVendor Vendor { get; set; }
        public GpuType Type { get; set; }
        public long VramBytes { get; set; }
        public int VramMB => (int)(VramBytes / 1024 / 1024);
        public string DriverVersion { get; set; } = "";
        public string PnpDeviceId { get; set; } = "";
        public bool SupportsEncoding { get; set; }
        public List<string> SupportedEncoders { get; set; } = new();
    }

    public class GpuWorkload
    {
        public int DeviceIndex { get; set; }
        public int ActiveEncoders { get; set; }
        public int EstimatedLoad { get; set; } // 0-100%
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }

    public class EncodingTask
    {
        public string Encoder { get; set; } = "h264_nvenc";
        public string OutputFile { get; set; } = "";
        public int Bitrate { get; set; } = 6000;
        public string ExtraArgs { get; set; } = "";
    }

    public class TaskResult
    {
        public EncodingTask Task { get; set; } = null!;
        public bool Success { get; set; }
        public string? Error { get; set; }
        public int? GpuUsed { get; set; }
    }

    public class ParallelEncodingResult
    {
        public List<TaskResult> TaskResults { get; set; } = new();
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public bool AllSucceeded => FailureCount == 0 && SuccessCount > 0;
    }

    #endregion
}
