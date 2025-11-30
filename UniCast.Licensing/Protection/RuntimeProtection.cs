using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UniCast.Licensing.Protection
{
    /// <summary>
    /// KATMAN 3: Runtime koruma - debugger, VM ve bellek manipülasyonu tespiti.
    /// </summary>
    public static class RuntimeProtection
    {
        private static volatile bool _isInitialized;
        private static volatile bool _threatDetected;
        private static Timer? _heartbeatTimer;

        public static event EventHandler<ThreatDetectedEventArgs>? ThreatDetected;

        #region Windows API Imports

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsDebuggerPresent();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, ref bool isDebuggerPresent);

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            ref IntPtr processInformation,
            int processInformationLength,
            ref int returnLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        #endregion

        /// <summary>
        /// Runtime korumasını başlatır.
        /// Uygulama başlangıcında bir kez çağrılmalı.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static void Initialize(int heartbeatIntervalMs = 30000)
        {
            if (_isInitialized)
                return;

            _isInitialized = true;

            // İlk kontrol
            PerformSecurityChecks();

            // Periyodik heartbeat
            _heartbeatTimer = new Timer(
                _ => PerformSecurityChecks(),
                null,
                heartbeatIntervalMs,
                heartbeatIntervalMs);
        }

        /// <summary>
        /// Korumanın aktif ve tehdit tespit edilmemiş olduğunu doğrular.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static bool IsSecure()
        {
            if (!_isInitialized)
                Initialize();

            PerformSecurityChecks();
            return !_threatDetected;
        }

        /// <summary>
        /// Tüm güvenlik kontrollerini çalıştırır.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static SecurityCheckResult PerformSecurityChecks()
        {
            var result = new SecurityCheckResult();

        #if DEBUG
            // DEBUG modunda tüm kontrolleri atla
            result.IsSecure = true;
            result.DebuggerAttached = false;
            result.VirtualMachine = false;
            result.Sandbox = false;
            result.TimingAnomaly = false;
            result.CrackToolDetected = false;
            return result;
        #else
            // 1. Debugger kontrolü
            result.DebuggerAttached = CheckDebugger();
            if (result.DebuggerAttached)
            {
                RaiseThreat(ThreatType.DebuggerDetected, "Debugger algılandı");
            }

            // 2. VM/Sandbox kontrolü
            result.VirtualMachine = CheckVirtualMachine();

            // 3. Sandbox kontrolü
            result.Sandbox = CheckSandbox();
            if (result.Sandbox)
            {
                RaiseThreat(ThreatType.SandboxDetected, "Sandbox ortamı algılandı");
            }

            // 4. Zamanlama anomalisi
            result.TimingAnomaly = CheckTimingAnomaly();
            if (result.TimingAnomaly)
            {
                RaiseThreat(ThreatType.TimingAnomaly, "Zamanlama anomalisi");
            }

            // 5. Crack araçları
            result.CrackToolDetected = CheckCrackTools();
            if (result.CrackToolDetected)
            {
                RaiseThreat(ThreatType.CrackToolDetected, "Crack aracı tespit edildi");
            }

            result.IsSecure = !result.DebuggerAttached &&
                            !result.Sandbox &&
                            !result.TimingAnomaly &&
                            !result.CrackToolDetected;

            _threatDetected = !result.IsSecure;
            return result;
        #endif
        }

        #region Security Checks

        private static bool CheckDebugger()
        {
            // Yöntem 1: Managed debugger
            if (Debugger.IsAttached)
                return true;

            // Yöntem 2: Windows API - yerel debugger
            if (IsDebuggerPresent())
                return true;

            // Yöntem 3: Remote debugger
            try
            {
                bool isRemoteDebugger = false;
                CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, ref isRemoteDebugger);
                if (isRemoteDebugger)
                    return true;
            }
            catch { }

            // Yöntem 4: NtQueryInformationProcess (ProcessDebugPort)
            try
            {
                IntPtr debugPort = IntPtr.Zero;
                int returnLength = 0;
                var status = NtQueryInformationProcess(
                    Process.GetCurrentProcess().Handle,
                    7, // ProcessDebugPort
                    ref debugPort,
                    IntPtr.Size,
                    ref returnLength);

                if (status == 0 && debugPort != IntPtr.Zero)
                    return true;
            }
            catch { }

            return false;
        }
        [SupportedOSPlatform("windows")]
        private static bool CheckVirtualMachine()
        {
            // VM üreticilerinin WMI değerleri
            string[] vmIndicators =
            {
                "VMware", "VirtualBox", "Virtual", "VBOX",
                "Hyper-V", "Xen", "QEMU", "KVM", "Parallels"
            };

            try
            {
                // BIOS kontrolü
                using var biosSearcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_BIOS");
                foreach (ManagementObject obj in biosSearcher.Get())
                {
                    var manufacturer = obj["Manufacturer"]?.ToString() ?? "";
                    var version = obj["Version"]?.ToString() ?? "";

                    foreach (var indicator in vmIndicators)
                    {
                        if (manufacturer.Contains(indicator, StringComparison.OrdinalIgnoreCase) ||
                            version.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }

                // Bilgisayar sistemi kontrolü
                using var systemSearcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_ComputerSystem");
                foreach (ManagementObject obj in systemSearcher.Get())
                {
                    var manufacturer = obj["Manufacturer"]?.ToString() ?? "";
                    var model = obj["Model"]?.ToString() ?? "";

                    foreach (var indicator in vmIndicators)
                    {
                        if (manufacturer.Contains(indicator, StringComparison.OrdinalIgnoreCase) ||
                            model.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }

                // Disk kontrolü
                using var diskSearcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_DiskDrive");
                foreach (ManagementObject obj in diskSearcher.Get())
                {
                    var model = obj["Model"]?.ToString() ?? "";
                    foreach (var indicator in vmIndicators)
                    {
                        if (model.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch { }

            // Registry kontrolü (VMware tools, VBox Guest Additions)
            try
            {
                var vmwareKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\VMware, Inc.\VMware Tools");
                if (vmwareKey != null)
                {
                    vmwareKey.Dispose();
                    return true;
                }

                var vboxKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Oracle\VirtualBox Guest Additions");
                if (vboxKey != null)
                {
                    vboxKey.Dispose();
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool CheckSandbox()
        {
            // Sandbox belirtileri
            try
            {
                // 1. Çok az dosya (sandbox genelde temiz)
                var desktopFiles = Directory.GetFiles(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
                if (desktopFiles.Length < 3)
                    return true; // Şüpheli

                // 2. Kısa uptime (sandbox yeni başlatılmış)
                var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
                if (uptime.TotalMinutes < 10)
                    return true; // Şüpheli

                // 3. Bilinen sandbox process'leri
                var sandboxProcesses = new[]
                {
                    "sandboxie", "sbiectrl", "sbiesvc",
                    "vmsrvc", "vmusrvc", "vmtoolsd",
                    "df5serv", "vboxservice", "vboxtray"
                };

                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        var name = proc.ProcessName.ToLowerInvariant();
                        foreach (var sandboxProc in sandboxProcesses)
                        {
                            if (name.Contains(sandboxProc))
                                return true;
                        }
                    }
                    catch { }
                }

                // 4. DLL kontrolü
                var sbiedll = GetModuleHandle("SbieDll.dll");
                if (sbiedll != IntPtr.Zero)
                    return true;
            }
            catch { }

            return false;
        }

        private static bool CheckTimingAnomaly()
        {
            // Debug altında bu işlem çok uzun sürer
            const int iterations = 100;
            const int maxExpectedMs = 50; // Normal: < 5ms

            var sw = Stopwatch.StartNew();

            for (int i = 0; i < iterations; i++)
            {
                // Basit işlem
                _ = Math.Sin(i) * Math.Cos(i);
            }

            sw.Stop();

            return sw.ElapsedMilliseconds > maxExpectedMs;
        }

        private static bool CheckCrackTools()
        {
            // Bilinen crack/keygen araçları
            string[] crackTools =
            {
                "ollydbg", "x64dbg", "x32dbg", "ida", "ida64",
                "immunitydebugger", "wireshark", "fiddler",
                "charles", "procmon", "procexp", "regmon",
                "apimonitor", "rohitab", "httpanalyzer",
                "dnspy", "ilspy", "jetbrains.dotpeek",
                "cheatengine", "artmoney", "tsearch"
            };

            try
            {
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        var name = proc.ProcessName.ToLowerInvariant();
                        foreach (var tool in crackTools)
                        {
                            if (name.Contains(tool))
                                return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return false;
        }

        #endregion

        private static void RaiseThreat(ThreatType type, string message)
        {
            _threatDetected = true;
            ThreatDetected?.Invoke(null, new ThreatDetectedEventArgs(type, message));
        }

        /// <summary>
        /// Kaynakları temizler.
        /// </summary>
        public static void Shutdown()
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            _isInitialized = false;
        }
    }

    /// <summary>
    /// Güvenlik kontrol sonucu.
    /// </summary>
    public sealed class SecurityCheckResult
    {
        public bool IsSecure { get; set; }
        public bool DebuggerAttached { get; set; }
        public bool VirtualMachine { get; set; }
        public bool Sandbox { get; set; }
        public bool TimingAnomaly { get; set; }
        public bool CrackToolDetected { get; set; }

        public string GetReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Security Status: {(IsSecure ? "✓ SECURE" : "✗ THREAT DETECTED")}");
            sb.AppendLine($"  Debugger:    {(DebuggerAttached ? "✗ DETECTED" : "✓ Clean")}");
            sb.AppendLine($"  VM:          {(VirtualMachine ? "⚠ Detected" : "✓ Physical")}");
            sb.AppendLine($"  Sandbox:     {(Sandbox ? "✗ DETECTED" : "✓ Clean")}");
            sb.AppendLine($"  Timing:      {(TimingAnomaly ? "✗ ANOMALY" : "✓ Normal")}");
            sb.AppendLine($"  Crack Tools: {(CrackToolDetected ? "✗ DETECTED" : "✓ Clean")}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Tehdit türleri.
    /// </summary>
    public enum ThreatType
    {
        DebuggerDetected,
        SandboxDetected,
        VirtualMachineDetected,
        TimingAnomaly,
        CrackToolDetected,
        MemoryTampered,
        IntegrityViolation
    }

    /// <summary>
    /// Tehdit tespit event argümanları.
    /// </summary>
    public sealed class ThreatDetectedEventArgs : EventArgs
    {
        public ThreatType Type { get; }
        public string Message { get; }
        public DateTime DetectedAt { get; }

        public ThreatDetectedEventArgs(ThreatType type, string message)
        {
            Type = type;
            Message = message;
            DetectedAt = DateTime.UtcNow;
        }
    }
}