using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace UniCast.Licensing.Hardware
{
    /// <summary>
    /// KATMAN 1: Makineye özgü benzersiz ve değiştirilemez kimlik oluşturur.
    /// Birden fazla donanım bileşeni kombine edilerek kopyalanması zorlaştırılır.
    /// </summary>
    public static class HardwareFingerprint
    {
        // Bileşen ağırlıkları (toplam = 100)
        private static readonly Dictionary<string, int> ComponentWeights = new()
        {
            ["CPU"] = 25,      // İşlemci ID - çok güvenilir
            ["BIOS"] = 20,     // BIOS seri no - güvenilir
            ["DISK"] = 20,     // Boot disk seri - güvenilir
            ["MAC"] = 15,      // MAC adresi - değişebilir ama önemli
            ["MB"] = 10,       // Anakart seri - güvenilir
            ["TPM"] = 10       // TPM varsa - çok güvenilir
        };

        private const int MIN_VALID_SCORE = 55; // En az bu kadar puan olmalı
        private const int SIMILARITY_THRESHOLD = 60; // Benzerlik eşiği

        // HMAC için gizli salt (obfuscation sonrası bile sabit kalmalı)
        private static readonly byte[] HmacKey =
        {
            0x55, 0x6E, 0x69, 0x43, 0x61, 0x73, 0x74, 0x4C,
            0x69, 0x63, 0x65, 0x6E, 0x73, 0x65, 0x32, 0x30,
            0x32, 0x35, 0x48, 0x57, 0x46, 0x50, 0x76, 0x31
        };

        #region Public API

        /// <summary>
        /// Tam Hardware ID üretir (64 karakter hex).
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static string Generate()
        {
            var components = CollectComponents();
            var raw = SerializeComponents(components);
            return ComputeHmacSha256(raw);
        }

        /// <summary>
        /// Kısa format ID (UI için) - XXXX-XXXX-XXXX-XXXX
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static string GenerateShort()
        {
            var full = Generate();
            var short16 = full.Substring(0, 16).ToUpperInvariant();
            return $"{short16[..4]}-{short16[4..8]}-{short16[8..12]}-{short16[12..]}";
        }

        /// <summary>
        /// Donanım bileşenlerini toplar ve doğrular.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static HardwareInfo Validate()
        {
            var info = new HardwareInfo();
            var components = CollectComponents();

            info.CpuId = components.GetValueOrDefault("CPU", "");
            info.BiosSerial = components.GetValueOrDefault("BIOS", "");
            info.DiskSerial = components.GetValueOrDefault("DISK", "");
            info.MacAddress = components.GetValueOrDefault("MAC", "");
            info.MotherboardSerial = components.GetValueOrDefault("MB", "");
            info.TpmPresent = components.ContainsKey("TPM");

            // Skor hesapla
            int score = 0;
            foreach (var kvp in components)
            {
                if (ComponentWeights.TryGetValue(kvp.Key, out var weight))
                    score += weight;
            }

            info.Score = score;
            info.IsValid = score >= MIN_VALID_SCORE;
            info.HardwareId = Generate();
            info.ShortId = GenerateShort();
            info.ComponentsRaw = SerializeComponents(components);
            info.MachineName = Environment.MachineName;

            return info;
        }

        /// <summary>
        /// İki donanım kimliğinin benzerlik oranını hesaplar (0-100).
        /// Küçük değişikliklerde (örn: yeni NIC) tolerans sağlar.
        /// </summary>
        public static int CalculateSimilarity(string storedRaw, string currentRaw)
        {
            if (string.IsNullOrEmpty(storedRaw) || string.IsNullOrEmpty(currentRaw))
                return 0;

            var stored = DeserializeComponents(storedRaw);
            var current = DeserializeComponents(currentRaw);

            int matchedWeight = 0;
            int totalWeight = 0;

            foreach (var kvp in ComponentWeights)
            {
                var key = kvp.Key;
                var weight = kvp.Value;

                var hasStored = stored.TryGetValue(key, out var storedVal);
                var hasCurrent = current.TryGetValue(key, out var currentVal);

                if (hasStored || hasCurrent)
                {
                    totalWeight += weight;

                    if (hasStored && hasCurrent && storedVal == currentVal)
                        matchedWeight += weight;
                }
            }

            return totalWeight > 0 ? (matchedWeight * 100) / totalWeight : 0;
        }

        /// <summary>
        /// Mevcut donanımın kayıtlı donanımla eşleşip eşleşmediğini kontrol eder.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static bool IsMatchingHardware(string storedRaw)
        {
            var currentRaw = Validate().ComponentsRaw;
            var similarity = CalculateSimilarity(storedRaw, currentRaw);
            return similarity >= SIMILARITY_THRESHOLD;
        }

        #endregion

        #region Component Collectors

        [SupportedOSPlatform("windows")]
        private static Dictionary<string, string> CollectComponents()
        {
            var components = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 1. CPU ID
            var cpuId = GetCpuId();
            if (!string.IsNullOrEmpty(cpuId))
                components["CPU"] = cpuId;

            // 2. BIOS Serial
            var biosSerial = GetBiosSerial();
            if (!string.IsNullOrEmpty(biosSerial))
                components["BIOS"] = biosSerial;

            // 3. Boot Disk Serial
            var diskSerial = GetDiskSerial();
            if (!string.IsNullOrEmpty(diskSerial))
                components["DISK"] = diskSerial;

            // 4. MAC Address
            var macAddress = GetPrimaryMacAddress();
            if (!string.IsNullOrEmpty(macAddress))
                components["MAC"] = macAddress;

            // 5. Motherboard Serial
            var mbSerial = GetMotherboardSerial();
            if (!string.IsNullOrEmpty(mbSerial))
                components["MB"] = mbSerial;

            // 6. TPM (varsa)
            var tpmId = GetTpmId();
            if (!string.IsNullOrEmpty(tpmId))
                components["TPM"] = tpmId;

            return components;
        }

        [SupportedOSPlatform("windows")]
        private static string GetCpuId()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessorId, Name FROM Win32_Processor");

                foreach (ManagementObject obj in searcher.Get())
                {
                    var id = obj["ProcessorId"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(id) && id != "0000000000000000")
                        return id;
                }
            }
            catch { }
            return "";
        }

        [SupportedOSPlatform("windows")]
        private static string GetBiosSerial()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT SerialNumber, Manufacturer FROM Win32_BIOS");

                foreach (ManagementObject obj in searcher.Get())
                {
                    var serial = obj["SerialNumber"]?.ToString()?.Trim();
                    if (IsValidSerial(serial))
                        return serial!;
                }
            }
            catch { }
            return "";
        }

        [SupportedOSPlatform("windows")]
        private static string GetDiskSerial()
        {
            try
            {
                // Boot disk (Index=0)
                using var searcher = new ManagementObjectSearcher(
                    "SELECT SerialNumber, Model FROM Win32_DiskDrive WHERE Index=0");

                foreach (ManagementObject obj in searcher.Get())
                {
                    var serial = obj["SerialNumber"]?.ToString()?.Trim().Replace(" ", "");
                    if (!string.IsNullOrEmpty(serial))
                        return serial;
                }
            }
            catch { }
            return "";
        }

        private static string GetPrimaryMacAddress()
        {
            try
            {
                // Fiziksel, aktif, non-virtual NIC bul
                var nic = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n =>
                        n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                        !IsVirtualAdapter(n.Description))
                    .OrderByDescending(n => n.Speed)
                    .ThenBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                    .FirstOrDefault();

                if (nic != null)
                {
                    var mac = nic.GetPhysicalAddress().ToString();
                    if (!string.IsNullOrEmpty(mac) && mac != "000000000000")
                        return mac;
                }
            }
            catch { }
            return "";
        }

        [SupportedOSPlatform("windows")]
        private static string GetMotherboardSerial()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT SerialNumber, Product FROM Win32_BaseBoard");

                foreach (ManagementObject obj in searcher.Get())
                {
                    var serial = obj["SerialNumber"]?.ToString()?.Trim();
                    if (IsValidSerial(serial))
                        return serial!;
                }
            }
            catch { }
            return "";
        }

        [SupportedOSPlatform("windows")]
        private static string GetTpmId()
        {
            try
            {
                // TPM 2.0 kontrolü
                using var searcher = new ManagementObjectSearcher(
                    @"root\cimv2\Security\MicrosoftTpm",
                    "SELECT * FROM Win32_Tpm");

                foreach (ManagementObject obj in searcher.Get())
                {
                    var specVersion = obj["SpecVersion"]?.ToString();
                    var manufacturerId = obj["ManufacturerId"]?.ToString();

                    if (!string.IsNullOrEmpty(specVersion))
                        return $"{manufacturerId ?? "TPM"}-{specVersion.Replace(",", ".")}";
                }
            }
            catch { }
            return "";
        }

        #endregion

        #region Helpers

        private static bool IsValidSerial(string? serial)
        {
            if (string.IsNullOrWhiteSpace(serial))
                return false;

            var invalidValues = new[]
            {
                "To Be Filled By O.E.M.",
                "Default string",
                "None",
                "N/A",
                "Not Specified",
                "System Serial Number",
                "0000000000",
                "123456789"
            };

            return !invalidValues.Any(v =>
                serial.Equals(v, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsVirtualAdapter(string description)
        {
            var virtualKeywords = new[]
            {
                "Virtual", "VPN", "Hyper-V", "VMware", "VirtualBox",
                "Tunnel", "Pseudo", "TAP-", "tun", "Docker"
            };

            return virtualKeywords.Any(k =>
                description.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        private static string SerializeComponents(Dictionary<string, string> components)
        {
            var parts = components
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => $"{kvp.Key}:{kvp.Value}");
            return string.Join("|", parts);
        }

        private static Dictionary<string, string> DeserializeComponents(string raw)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(raw))
                return dict;

            foreach (var part in raw.Split('|'))
            {
                var colonIdx = part.IndexOf(':');
                if (colonIdx > 0)
                {
                    var key = part[..colonIdx];
                    var value = part[(colonIdx + 1)..];
                    dict[key] = value;
                }
            }

            return dict;
        }

        private static string ComputeHmacSha256(string input)
        {
            using var hmac = new HMACSHA256(HmacKey);
            var inputBytes = Encoding.UTF8.GetBytes(input);
            var hashBytes = hmac.ComputeHash(inputBytes);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        #endregion
    }

    /// <summary>
    /// Donanım bilgisi sonucu.
    /// </summary>
    public sealed class HardwareInfo
    {
        public bool IsValid { get; set; }
        public int Score { get; set; }
        public string HardwareId { get; set; } = "";
        public string ShortId { get; set; } = "";
        public string ComponentsRaw { get; set; } = "";
        public string MachineName { get; set; } = "";

        public string CpuId { get; set; } = "";
        public string BiosSerial { get; set; } = "";
        public string DiskSerial { get; set; } = "";
        public string MacAddress { get; set; } = "";
        public string MotherboardSerial { get; set; } = "";
        public bool TpmPresent { get; set; }

        public string GetDiagnosticsReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("╔════════════════════════════════════════════════════╗");
            sb.AppendLine("║         HARDWARE FINGERPRINT REPORT                ║");
            sb.AppendLine("╠════════════════════════════════════════════════════╣");
            sb.AppendLine($"║ Machine:    {MachineName,-39}║");
            sb.AppendLine($"║ Status:     {(IsValid ? "✓ VALID" : "✗ INVALID"),-39}║");
            sb.AppendLine($"║ Score:      {Score}/100 (minimum 55)                  ║");
            sb.AppendLine("╠════════════════════════════════════════════════════╣");
            sb.AppendLine("║ COMPONENTS                                         ║");
            sb.AppendLine($"║   CPU ID:      {Check(!string.IsNullOrEmpty(CpuId))} {Mask(CpuId),-30}║");
            sb.AppendLine($"║   BIOS:        {Check(!string.IsNullOrEmpty(BiosSerial))} {Mask(BiosSerial),-30}║");
            sb.AppendLine($"║   Disk:        {Check(!string.IsNullOrEmpty(DiskSerial))} {Mask(DiskSerial),-30}║");
            sb.AppendLine($"║   MAC:         {Check(!string.IsNullOrEmpty(MacAddress))} {Mask(MacAddress),-30}║");
            sb.AppendLine($"║   Motherboard: {Check(!string.IsNullOrEmpty(MotherboardSerial))} {Mask(MotherboardSerial),-30}║");
            sb.AppendLine($"║   TPM:         {Check(TpmPresent)} {(TpmPresent ? "Present" : "Not found"),-30}║");
            sb.AppendLine("╠════════════════════════════════════════════════════╣");
            sb.AppendLine($"║ Hardware ID: {ShortId,-38}║");
            sb.AppendLine("╚════════════════════════════════════════════════════╝");
            return sb.ToString();

            static string Check(bool ok) => ok ? "✓" : "✗";
            static string Mask(string s) => string.IsNullOrEmpty(s) ? "(none)" :
                s.Length <= 8 ? s : $"{s[..4]}...{s[^4..]}";
        }
    }
}