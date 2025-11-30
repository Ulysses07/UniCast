using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UniCast.Licensing.Hardware
{
    /// <summary>
    /// KATMAN 1: Makineye özgü donanım parmak izi oluşturur.
    /// Çoklu donanım bileşeni kullanarak sağlam bir kimlik üretir.
    /// Küçük donanım değişikliklerine toleranslıdır.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class HardwareFingerprint
    {
        // Bileşen ağırlıkları (toplam: 100)
        private static readonly Dictionary<string, int> ComponentWeights = new()
        {
            { "CPU", 25 },           // Nadiren değişir
            { "Motherboard", 25 },   // Çok nadiren değişir
            { "BIOS", 15 },          // Neredeyse hiç değişmez
            { "MAC", 20 },           // Bazen değişebilir
            { "Disk", 15 }           // Sık değişebilir
        };

        private const int MinimumValidScore = 60; // %60 eşleşme yeterli

        /// <summary>
        /// Tam donanım ID'si oluşturur (SHA256 hash).
        /// </summary>
        public static string Generate()
        {
            var result = Validate();
            return result.HardwareId;
        }

        /// <summary>
        /// Kısa donanım ID'si oluşturur (ilk 16 karakter).
        /// UI ve log için kullanışlı.
        /// </summary>
        public static string GenerateShort()
        {
            var fullId = Generate();
            return fullId.Length >= 16 ? fullId[..16] : fullId;
        }

        /// <summary>
        /// Donanım bilgilerini toplar ve doğrular.
        /// </summary>
        public static HardwareValidationResult Validate()
        {
            var result = new HardwareValidationResult();
            var components = new Dictionary<string, ComponentInfo>();
            int totalScore = 0;

            // 1. CPU bilgisi
            var cpuInfo = GetCpuInfo();
            components["CPU"] = cpuInfo;
            if (!string.IsNullOrEmpty(cpuInfo.Value))
                totalScore += ComponentWeights["CPU"];

            // 2. Anakart bilgisi
            var mbInfo = GetMotherboardInfo();
            components["Motherboard"] = mbInfo;
            if (!string.IsNullOrEmpty(mbInfo.Value))
                totalScore += ComponentWeights["Motherboard"];

            // 3. BIOS bilgisi
            var biosInfo = GetBiosInfo();
            components["BIOS"] = biosInfo;
            if (!string.IsNullOrEmpty(biosInfo.Value))
                totalScore += ComponentWeights["BIOS"];

            // 4. MAC adresi
            var macInfo = GetMacAddress();
            components["MAC"] = macInfo;
            if (!string.IsNullOrEmpty(macInfo.Value))
                totalScore += ComponentWeights["MAC"];

            // 5. Disk seri numarası
            var diskInfo = GetDiskSerial();
            components["Disk"] = diskInfo;
            if (!string.IsNullOrEmpty(diskInfo.Value))
                totalScore += ComponentWeights["Disk"];

            // Bileşenleri JSON'a çevir (karşılaştırma için)
            result.ComponentsRaw = SerializeComponents(components);

            // Hash oluştur
            var combinedData = string.Join("|", components
                .OrderBy(c => c.Key)
                .Select(c => $"{c.Key}:{c.Value.Value}"));

            result.HardwareId = ComputeHash(combinedData);
            result.ShortId = result.HardwareId[..16];
            result.Score = totalScore;
            result.IsValid = totalScore >= MinimumValidScore;
            result.Components = components;

            return result;
        }

        /// <summary>
        /// İki bileşen seti arasındaki benzerliği hesaplar.
        /// </summary>
        public static int CalculateSimilarity(string? componentsJson1, string? componentsJson2)
        {
            if (string.IsNullOrEmpty(componentsJson1) || string.IsNullOrEmpty(componentsJson2))
                return 0;

            try
            {
                var components1 = DeserializeComponents(componentsJson1);
                var components2 = DeserializeComponents(componentsJson2);

                if (components1 == null || components2 == null)
                    return 0;

                int matchScore = 0;

                foreach (var weight in ComponentWeights)
                {
                    var key = weight.Key;
                    var weightValue = weight.Value;

                    if (components1.TryGetValue(key, out var comp1) &&
                        components2.TryGetValue(key, out var comp2))
                    {
                        if (!string.IsNullOrEmpty(comp1.Value) &&
                            comp1.Value == comp2.Value)
                        {
                            matchScore += weightValue;
                        }
                    }
                }

                return matchScore;
            }
            catch
            {
                return 0;
            }
        }

        #region Component Collection

        private static ComponentInfo GetCpuInfo()
        {
            var info = new ComponentInfo { Name = "CPU" };

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessorId, Name, NumberOfCores FROM Win32_Processor");

                foreach (ManagementObject obj in searcher.Get())
                {
                    var processorId = obj["ProcessorId"]?.ToString()?.Trim();
                    var name = obj["Name"]?.ToString()?.Trim();
                    var cores = obj["NumberOfCores"]?.ToString();

                    if (!string.IsNullOrEmpty(processorId))
                    {
                        info.Value = processorId;
                        info.Details = $"{name} ({cores} cores)";
                        info.IsAvailable = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }

            return info;
        }

        private static ComponentInfo GetMotherboardInfo()
        {
            var info = new ComponentInfo { Name = "Motherboard" };

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT SerialNumber, Manufacturer, Product FROM Win32_BaseBoard");

                foreach (ManagementObject obj in searcher.Get())
                {
                    var serial = obj["SerialNumber"]?.ToString()?.Trim();
                    var manufacturer = obj["Manufacturer"]?.ToString()?.Trim();
                    var product = obj["Product"]?.ToString()?.Trim();

                    // "To Be Filled" gibi placeholder değerleri atla
                    if (!string.IsNullOrEmpty(serial) &&
                        !serial.Contains("To Be", StringComparison.OrdinalIgnoreCase) &&
                        !serial.Equals("Default string", StringComparison.OrdinalIgnoreCase))
                    {
                        info.Value = serial;
                        info.Details = $"{manufacturer} {product}";
                        info.IsAvailable = true;
                        break;
                    }

                    // Serial yoksa manufacturer + product kullan
                    if (!string.IsNullOrEmpty(manufacturer) && !string.IsNullOrEmpty(product))
                    {
                        info.Value = ComputeHash($"{manufacturer}|{product}")[..16];
                        info.Details = $"{manufacturer} {product}";
                        info.IsAvailable = true;
                    }
                }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }

            return info;
        }

        private static ComponentInfo GetBiosInfo()
        {
            var info = new ComponentInfo { Name = "BIOS" };

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT SerialNumber, Manufacturer, Version FROM Win32_BIOS");

                foreach (ManagementObject obj in searcher.Get())
                {
                    var serial = obj["SerialNumber"]?.ToString()?.Trim();
                    var manufacturer = obj["Manufacturer"]?.ToString()?.Trim();
                    var version = obj["Version"]?.ToString()?.Trim();

                    if (!string.IsNullOrEmpty(serial) &&
                        !serial.Contains("To Be", StringComparison.OrdinalIgnoreCase))
                    {
                        info.Value = serial;
                        info.Details = $"{manufacturer} v{version}";
                        info.IsAvailable = true;
                        break;
                    }

                    // Serial yoksa manufacturer + version kullan
                    if (!string.IsNullOrEmpty(manufacturer))
                    {
                        info.Value = ComputeHash($"{manufacturer}|{version}")[..16];
                        info.Details = $"{manufacturer} v{version}";
                        info.IsAvailable = true;
                    }
                }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }

            return info;
        }

        private static ComponentInfo GetMacAddress()
        {
            var info = new ComponentInfo { Name = "MAC" };

            try
            {
                var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic =>
                        nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                        !nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                        !nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) &&
                        nic.OperationalStatus == OperationalStatus.Up)
                    .OrderByDescending(nic =>
                        nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 2 :
                        nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)
                    .FirstOrDefault();

                if (networkInterface != null)
                {
                    var mac = networkInterface.GetPhysicalAddress().ToString();
                    if (!string.IsNullOrEmpty(mac) && mac != "000000000000")
                    {
                        info.Value = mac;
                        info.Details = $"{networkInterface.Name} ({networkInterface.Description})";
                        info.IsAvailable = true;
                    }
                }

                // Yedek: Herhangi bir fiziksel MAC bul
                if (!info.IsAvailable)
                {
                    var anyNic = NetworkInterface.GetAllNetworkInterfaces()
                        .FirstOrDefault(nic =>
                            nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                            nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);

                    if (anyNic != null)
                    {
                        var mac = anyNic.GetPhysicalAddress().ToString();
                        if (!string.IsNullOrEmpty(mac) && mac != "000000000000")
                        {
                            info.Value = mac;
                            info.Details = $"{anyNic.Name} (inactive)";
                            info.IsAvailable = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }

            return info;
        }

        private static ComponentInfo GetDiskSerial()
        {
            var info = new ComponentInfo { Name = "Disk" };

            try
            {
                // Windows sürücüsünü bul
                var systemDrive = Environment.GetFolderPath(Environment.SpecialFolder.System)[0];

                using var searcher = new ManagementObjectSearcher(
                    $"SELECT SerialNumber, Model, Size FROM Win32_DiskDrive WHERE DeviceID LIKE '%PHYSICALDRIVE0%'");

                foreach (ManagementObject obj in searcher.Get())
                {
                    var serial = obj["SerialNumber"]?.ToString()?.Trim();
                    var model = obj["Model"]?.ToString()?.Trim();
                    var size = obj["Size"] != null ?
                        (Convert.ToInt64(obj["Size"]) / (1024 * 1024 * 1024)).ToString() + "GB" :
                        "Unknown";

                    if (!string.IsNullOrEmpty(serial))
                    {
                        // Bazı sürücüler ters serial döndürür, normalize et
                        info.Value = NormalizeDiskSerial(serial);
                        info.Details = $"{model} ({size})";
                        info.IsAvailable = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }

            return info;
        }

        #endregion

        #region Helpers

        private static string NormalizeDiskSerial(string serial)
        {
            if (string.IsNullOrEmpty(serial))
                return "";

            // Bazı sürücüler her iki karakteri ters çevirir
            // "1234" -> "2143" gibi
            serial = serial.Trim().Replace(" ", "");

            // Sadece alfanumerik karakterleri al
            var normalized = new string(serial.Where(char.IsLetterOrDigit).ToArray());

            return normalized.ToUpperInvariant();
        }

        private static string ComputeHash(string input)
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }

        private static string SerializeComponents(Dictionary<string, ComponentInfo> components)
        {
            var simplified = components.ToDictionary(
                kvp => kvp.Key,
                kvp => new { kvp.Value.Value, kvp.Value.IsAvailable });

            return JsonSerializer.Serialize(simplified);
        }

        private static Dictionary<string, ComponentInfo>? DeserializeComponents(string json)
        {
            try
            {
                var simplified = JsonSerializer.Deserialize<Dictionary<string, ComponentInfo>>(json);
                return simplified;
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }

    /// <summary>
    /// Donanım bileşeni bilgisi.
    /// </summary>
    public sealed class ComponentInfo
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public string? Details { get; set; }
        public bool IsAvailable { get; set; }
        public string? Error { get; set; }

        public override string ToString()
        {
            if (!IsAvailable)
                return $"{Name}: N/A ({Error ?? "Not found"})";

            return $"{Name}: {Value[..Math.Min(8, Value.Length)]}... ({Details})";
        }
    }

    /// <summary>
    /// Donanım doğrulama sonucu.
    /// </summary>
    public sealed class HardwareValidationResult
    {
        public string HardwareId { get; set; } = "";
        public string ShortId { get; set; } = "";
        public int Score { get; set; }
        public bool IsValid { get; set; }
        public string ComponentsRaw { get; set; } = "";
        public Dictionary<string, ComponentInfo> Components { get; set; } = new();

        /// <summary>
        /// Tanılama raporu oluşturur.
        /// </summary>
        public string GetDiagnosticsReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine("Hardware Fingerprint Diagnostics Report");
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine($"Hardware ID: {HardwareId}");
            sb.AppendLine($"Short ID:    {ShortId}");
            sb.AppendLine($"Score:       {Score}/100 ({(IsValid ? "VALID" : "INVALID")})");
            sb.AppendLine("───────────────────────────────────────────");
            sb.AppendLine("Components:");

            foreach (var component in Components.OrderByDescending(c => c.Value.IsAvailable))
            {
                var status = component.Value.IsAvailable ? "✓" : "✗";
                sb.AppendLine($"  [{status}] {component.Value}");
            }

            sb.AppendLine("═══════════════════════════════════════════");
            return sb.ToString();
        }
    }
}