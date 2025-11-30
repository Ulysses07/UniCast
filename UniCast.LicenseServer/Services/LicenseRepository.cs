using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UniCast.LicenseServer.Services
{
    /// <summary>
    /// Lisans repository interface
    /// </summary>
    public interface ILicenseRepository
    {
        Task<LicenseData?> FindByIdAsync(string licenseId);
        Task<LicenseData?> FindByKeyAsync(string licenseKey);
        Task SaveAsync(LicenseData license);
        Task DeleteAsync(string licenseId);
        Task<IEnumerable<LicenseData>> GetAllAsync();
    }

    /// <summary>
    /// JSON dosya tabanlı lisans repository
    /// </summary>
    public class LicenseRepository : ILicenseRepository
    {
        private readonly string _dataPath;
        private readonly ConcurrentDictionary<string, LicenseData> _cache = new();
        private readonly SemaphoreSlim _fileLock = new(1, 1);
        private bool _isLoaded;

        public LicenseRepository()
        {
            _dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "licenses.json");
            var dir = Path.GetDirectoryName(_dataPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        private async Task EnsureLoadedAsync()
        {
            if (_isLoaded) return;

            await _fileLock.WaitAsync();
            try
            {
                if (_isLoaded) return;

                if (File.Exists(_dataPath))
                {
                    var json = await File.ReadAllTextAsync(_dataPath);
                    var licenses = JsonSerializer.Deserialize<List<LicenseData>>(json) ?? new List<LicenseData>();

                    foreach (var license in licenses)
                    {
                        _cache[license.LicenseId] = license;
                    }
                }

                _isLoaded = true;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private async Task PersistAsync()
        {
            await _fileLock.WaitAsync();
            try
            {
                var licenses = _cache.Values.ToList();
                var json = JsonSerializer.Serialize(licenses, new JsonSerializerOptions { WriteIndented = true });

                var tempPath = _dataPath + ".tmp";
                await File.WriteAllTextAsync(tempPath, json);
                File.Move(tempPath, _dataPath, true);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public async Task<LicenseData?> FindByIdAsync(string licenseId)
        {
            await EnsureLoadedAsync();
            return _cache.TryGetValue(licenseId, out var license) ? license : null;
        }

        public async Task<LicenseData?> FindByKeyAsync(string licenseKey)
        {
            await EnsureLoadedAsync();
            return _cache.Values.FirstOrDefault(l =>
                string.Equals(l.LicenseKey, licenseKey, StringComparison.OrdinalIgnoreCase));
        }

        public async Task SaveAsync(LicenseData license)
        {
            await EnsureLoadedAsync();
            _cache[license.LicenseId] = license;
            await PersistAsync();
        }

        public async Task DeleteAsync(string licenseId)
        {
            await EnsureLoadedAsync();
            _cache.TryRemove(licenseId, out _);
            await PersistAsync();
        }

        public async Task<IEnumerable<LicenseData>> GetAllAsync()
        {
            await EnsureLoadedAsync();
            return _cache.Values.ToList();
        }
    }

    /// <summary>
    /// Lisans verisi modeli
    /// </summary>
    public class LicenseData
    {
        public string LicenseId { get; set; } = string.Empty;
        public string LicenseKey { get; set; } = string.Empty;
        public string Type { get; set; } = "Trial";
        public long Features { get; set; }
        public string LicenseeName { get; set; } = string.Empty;
        public string LicenseeEmail { get; set; } = string.Empty;
        public DateTime IssuedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public int MaxMachines { get; set; } = 1;
        public List<HardwareActivation> Activations { get; set; } = new();
        public string? Signature { get; set; }
        public bool IsRevoked { get; set; }
        public DateTime? RevokedAtUtc { get; set; }
        public DateTime? LastValidationUtc { get; set; }
    }

    /// <summary>
    /// Donanım aktivasyonu modeli
    /// </summary>
    public class HardwareActivation
    {
        public string HardwareId { get; set; } = string.Empty;
        public string HardwareIdShort { get; set; } = string.Empty;
        public string? MachineName { get; set; }
        public string? ComponentsHash { get; set; }
        public DateTime ActivatedAtUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public string? IpAddress { get; set; }
        public string? OsVersion { get; set; }
    }
}