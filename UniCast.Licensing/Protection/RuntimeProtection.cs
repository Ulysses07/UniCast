using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace UniCast.Licensing.Protection
{
    /// <summary>
    /// KATMAN 3: Runtime koruma - debugger, VM ve bellek manipülasyonu tespiti.
    /// DEBUG modunda tüm kontroller atlanır.
    /// </summary>
    public static class RuntimeProtection
    {
        private static volatile bool _isInitialized;
#pragma warning disable CS0414 // Field is assigned but never used (DEBUG modunda kullanılmıyor)
        private static volatile bool _threatDetected;
#pragma warning restore CS0414
        private static Timer? _heartbeatTimer;
        private static readonly object _initLock = new();

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

            lock (_initLock)
            {
                if (_isInitialized)
                    return;

#if DEBUG
                System.Diagnostics.Debug.WriteLine("[RuntimeProtection] DEBUG modu - Koruma atlanıyor");
                _isInitialized = true;
                return;
#else
                // İlk kontrol
                PerformSecurityChecks();

                // Periyodik heartbeat
                _heartbeatTimer = new Timer(
                    _ => 
                    {
                        try { PerformSecurityChecks(); }
                        catch (Exception ex) 
                        { 
                            System.Diagnostics.Debug.WriteLine($"[RuntimeProtection] Heartbeat hatası: {ex.Message}");
                        }
                    },
                    null,
                    heartbeatIntervalMs,
                    heartbeatIntervalMs);

                _isInitialized = true;
#endif
            }
        }

        /// <summary>
        /// Korumanın aktif ve tehdit tespit edilmemiş olduğunu doğrular.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static bool IsSecure()
        {
#if DEBUG
            return true; // DEBUG modunda her zaman güvenli
#else
            if (!_isInitialized)
                Initialize();

            PerformSecurityChecks();
            return !_threatDetected;
#endif
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
            try
            {
                // 1. Debugger kontrolü
                result.DebuggerAttached = CheckDebugger();
                if (result.DebuggerAttached)
                {
                    RaiseThreat(ThreatType.DebuggerDetected, "Debugger algılandı");
                }

                // 2. VM/Sandbox kontrolü
                result.VirtualMachine = CheckVirtualMachine();
                // VM'de çalışmaya izin ver ama logla
                if (result.VirtualMachine)
                {
                    System.Diagnostics.Debug.WriteLine("[RuntimeProtection] VM ortamı tespit edildi");
                }

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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RuntimeProtection] Güvenlik kontrolü hatası: {ex.Message}");
                // Hata durumunda güvenli kabul et (false positive önleme)
                result.IsSecure = true;
            }

            return result;
#endif
        }

        #region Security Checks

        private static bool CheckDebugger()
        {
            try
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
                    using var currentProcess = Process.GetCurrentProcess();
                    CheckRemoteDebuggerPresent(currentProcess.Handle, ref isRemoteDebugger);
                    if (isRemoteDebugger)
                        return true;
                }
                catch (Exception ex)
                {
                    // DÜZELTME v26: Boş catch'e loglama eklendi
                    System.Diagnostics.Debug.WriteLine($"[RuntimeProtection.CheckDebugger] Remote debugger kontrolü hatası: {ex.Message}");
                }

                // Yöntem 4: NtQueryInformationProcess (ProcessDebugPort)
                try
                {
                    IntPtr debugPort = IntPtr.Zero;
                    int returnLength = 0;
                    using var currentProcess = Process.GetCurrentProcess();
                    var status = NtQueryInformationProcess(
                        currentProcess.Handle,
                        7, // ProcessDebugPort
                        ref debugPort,
                        IntPtr.Size,
                        ref returnLength);

                    if (status == 0 && debugPort != IntPtr.Zero)
                        return true;
                }
                catch (Exception ex)
                {
                    // DÜZELTME v26: Boş catch'e loglama eklendi
                    System.Diagnostics.Debug.WriteLine($"[RuntimeProtection.CheckDebugger] NtQuery kontrolü hatası: {ex.Message}");
                }

                return false;
            }
            catch
            {
                return false; // Hata durumunda false positive önleme
            }
        }

        [SupportedOSPlatform("windows")]
        private static bool CheckVirtualMachine()
        {
            try
            {
                // VM üreticilerinin WMI değerleri
                string[] vmIndicators =
                {
                    "VMware", "VirtualBox", "Virtual", "VBOX",
                    "Hyper-V", "Xen", "QEMU", "KVM", "Parallels"
                };

                // BIOS kontrolü
                using var biosSearcher = new ManagementObjectSearcher(
                    "SELECT Manufacturer, Version FROM Win32_BIOS");

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
                    "SELECT Manufacturer, Model FROM Win32_ComputerSystem");

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

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool CheckSandbox()
        {
            try
            {
                // 1. Bilinen sandbox process'leri
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
                            {
                                proc.Dispose();
                                return true;
                            }
                        }
                        proc.Dispose();
                    }
                    catch (Exception ex)
                    {
                        // DÜZELTME v26: Boş catch'e loglama eklendi
                        System.Diagnostics.Debug.WriteLine($"[RuntimeProtection.CheckSandbox] Process kontrolü hatası: {ex.Message}");
                        try { proc.Dispose(); } catch (Exception disposeEx) { System.Diagnostics.Debug.WriteLine($"[RuntimeProtection.CheckSandbox] Process dispose hatası: {disposeEx.Message}"); }
                    }
                }

                // 2. DLL kontrolü
                var sbiedll = GetModuleHandle("SbieDll.dll");
                if (sbiedll != IntPtr.Zero)
                    return true;

                // 3. Sandboxie registry kontrolü
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Sandboxie");
                    if (key != null)
                        return true;
                }
                catch (Exception ex)
                {
                    // DÜZELTME v26: Boş catch'e loglama eklendi
                    System.Diagnostics.Debug.WriteLine($"[RuntimeProtection.CheckSandbox] Registry kontrolü hatası: {ex.Message}");
                }

                return false;
            }
            catch (Exception ex)
            {
                // DÜZELTME v26: Boş catch'e loglama eklendi
                System.Diagnostics.Debug.WriteLine($"[RuntimeProtection.CheckSandbox] Genel hata: {ex.Message}");
                return false;
            }
        }

        private static bool CheckTimingAnomaly()
        {
            try
            {
                // Debug altında bu işlem çok uzun sürer
                const int iterations = 100;
                const int maxExpectedMs = 100; // Normal: < 10ms, toleranslı

                var sw = Stopwatch.StartNew();

                for (int i = 0; i < iterations; i++)
                {
                    // Basit işlem
                    _ = Math.Sin(i) * Math.Cos(i);
                }

                sw.Stop();

                return sw.ElapsedMilliseconds > maxExpectedMs;
            }
            catch (Exception ex)
            {
                // DÜZELTME v27: Exception logging eklendi
                System.Diagnostics.Debug.WriteLine($"[RuntimeProtection] CheckTimingAnomaly exception: {ex.Message}");
                return false; // Hata durumunda false positive önle
            }
        }

        private static bool CheckCrackTools()
        {
            try
            {
                // DÜZELTME v17.2: Sadece gerçek crack/keygen araçları
                // procmon, procexp gibi Sysinternals araçları çıkarıldı (legitimate sysadmin araçları)
                // dnspy, ilspy, dotpeek çıkarıldı (legitimate .NET developer araçları)
                string[] crackTools =
                {
                    "ollydbg", "x64dbg", "x32dbg",       // Debuggers (cracking için kullanılır)
                    "immunitydebugger",                   // Exploit geliştirme
                    "cheatengine", "artmoney",           // Memory manipulation
                    "keygen", "crack", "patch",          // Explicit crack araçları
                    "regmonitor"                          // Registry manipulation
                };

                // DÜZELTME v17.2: Whitelist - Bu araçlar tespit edilse bile ignore edilir
                // Kullanıcı settings dosyasına whitelist ekleyebilir
                var whitelistedProcesses = GetWhitelistedProcesses();

                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        var name = proc.ProcessName.ToLowerInvariant();

                        // Whitelist kontrolü
                        if (whitelistedProcesses.Contains(name))
                        {
                            proc.Dispose();
                            continue;
                        }

                        foreach (var tool in crackTools)
                        {
                            if (name.Contains(tool))
                            {
                                proc.Dispose();
                                return true;
                            }
                        }
                        proc.Dispose();
                    }
                    catch (Exception ex)
                    {
                        // DÜZELTME v26: Boş catch'e loglama eklendi
                        System.Diagnostics.Debug.WriteLine($"[RuntimeProtection.CheckCrackTools] Process kontrolü hatası: {ex.Message}");
                        try { proc.Dispose(); } catch (Exception disposeEx) { System.Diagnostics.Debug.WriteLine($"[RuntimeProtection.CheckCrackTools] Process dispose hatası: {disposeEx.Message}"); }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                // DÜZELTME v26: Boş catch'e loglama eklendi
                System.Diagnostics.Debug.WriteLine($"[RuntimeProtection.CheckCrackTools] Genel hata: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// DÜZELTME v17.2: Kullanıcının whitelist'e aldığı process'leri döndürür.
        /// </summary>
        private static HashSet<string> GetWhitelistedProcesses()
        {
            // Varsayılan whitelist - meşru geliştirici ve sysadmin araçları
            var whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // Sysinternals araçları
                "procmon", "procmon64", "procexp", "procexp64",
                "autoruns", "autoruns64", "tcpview", "tcpview64",
                "pslist", "pslist64", "listdlls", "listdlls64",
                
                // .NET geliştirme araçları
                "dnspy", "ilspy", "dotpeek", "dotpeek64",
                "ildasm", "peverify",
                
                // IDE ve debugger'lar (normal geliştirme için)
                "devenv", "code", "rider", "rider64",
                
                // API test araçları
                "apimonitor", "fiddler", "wireshark", "charles"
            };

            // TODO: Kullanıcı settings'den ek whitelist yüklenebilir
            // var userWhitelist = SettingsStore.Data?.WhitelistedProcesses ?? Array.Empty<string>();
            // foreach (var proc in userWhitelist) whitelist.Add(proc);

            return whitelist;
        }

        #endregion

        private static void RaiseThreat(ThreatType type, string message)
        {
            _threatDetected = true;

            try
            {
                ThreatDetected?.Invoke(null, new ThreatDetectedEventArgs(type, message));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RuntimeProtection] ThreatDetected event hatası: {ex.Message}");
            }
        }

        /// <summary>
        /// Kaynakları temizler.
        /// </summary>
        public static void Shutdown()
        {
            lock (_initLock)
            {
                _heartbeatTimer?.Dispose();
                _heartbeatTimer = null;
                _isInitialized = false;
                _threatDetected = false;
                ThreatDetected = null;
            }
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