using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Timer = System.Threading.Timer;

namespace UniCast.App.Services
{
    /// <summary>
    /// DÜZELTME v19: Otomatik güncelleme servisi
    /// GitHub Releases veya custom update server desteği
    /// </summary>
    public sealed class AutoUpdateService : IDisposable
    {
        #region Singleton

        private static readonly Lazy<AutoUpdateService> _instance =
            new(() => new AutoUpdateService(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static AutoUpdateService Instance => _instance.Value;

        #endregion

        #region Configuration

        private static class Config
        {
            public const string UpdateServerUrl = "https://api.github.com/repos/unicast-app/unicast/releases/latest";
            public const string BackupUpdateUrl = "https://update.unicast.app/check";
            public const int CheckIntervalHours = 6;
            public const int DownloadTimeoutMinutes = 10;
            public const string UpdateFolderName = "Updates";
        }

        #endregion

        #region Fields

        private readonly HttpClient _httpClient;
        private readonly Timer _checkTimer;
        private readonly string _currentVersion;
        private readonly string _updateFolder;

        private UpdateInfo? _availableUpdate;
        private bool _isChecking;
        private bool _isDownloading;
        private bool _disposed;

        #endregion

        #region Events

        public event EventHandler<UpdateAvailableEventArgs>? OnUpdateAvailable;
        public event EventHandler<UpdateProgressEventArgs>? OnDownloadProgress;
        public event EventHandler<UpdateReadyEventArgs>? OnUpdateReady;
        public event EventHandler<UpdateErrorEventArgs>? OnUpdateError;

        #endregion

        #region Properties

        public bool IsUpdateAvailable => _availableUpdate != null;
        public UpdateInfo? AvailableUpdate => _availableUpdate;
        public bool IsChecking => _isChecking;
        public bool IsDownloading => _isDownloading;

        #endregion

        #region Constructor

        private AutoUpdateService()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(Config.DownloadTimeoutMinutes)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "UniCast-Updater");

            _currentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

            _updateFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UniCast", Config.UpdateFolderName);

            Directory.CreateDirectory(_updateFolder);

            _checkTimer = new Timer(
                async _ => await CheckForUpdatesAsync(),
                null,
                TimeSpan.FromMinutes(5), // İlk kontrol 5 dakika sonra
                TimeSpan.FromHours(Config.CheckIntervalHours));
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Manuel güncelleme kontrolü
        /// </summary>
        public async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
        {
            if (_isChecking) return _availableUpdate;

            _isChecking = true;

            try
            {
                Log.Information("[AutoUpdate] Güncelleme kontrol ediliyor...");

                var updateInfo = await FetchUpdateInfoAsync(ct);

                if (updateInfo != null && IsNewerVersion(updateInfo.Version))
                {
                    _availableUpdate = updateInfo;

                    Log.Information("[AutoUpdate] Yeni sürüm mevcut: {Version}", updateInfo.Version);

                    OnUpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs
                    {
                        UpdateInfo = updateInfo
                    });

                    return updateInfo;
                }

                Log.Debug("[AutoUpdate] Güncel sürüm kullanılıyor: {Version}", _currentVersion);
                return null;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[AutoUpdate] Güncelleme kontrolü başarısız");

                OnUpdateError?.Invoke(this, new UpdateErrorEventArgs
                {
                    Error = ex,
                    Phase = UpdatePhase.Checking
                });

                return null;
            }
            finally
            {
                _isChecking = false;
            }
        }

        /// <summary>
        /// Güncellemeyi indir
        /// </summary>
        public async Task<bool> DownloadUpdateAsync(CancellationToken ct = default)
        {
            if (_availableUpdate == null)
            {
                Log.Warning("[AutoUpdate] İndirilecek güncelleme yok");
                return false;
            }

            if (_isDownloading) return false;

            _isDownloading = true;

            try
            {
                var downloadUrl = _availableUpdate.DownloadUrl;
                var fileName = $"UniCast_{_availableUpdate.Version}.exe";
                var filePath = Path.Combine(_updateFolder, fileName);

                Log.Information("[AutoUpdate] İndiriliyor: {Url}", downloadUrl);

                using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                var downloadedBytes = 0L;

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    downloadedBytes += bytesRead;

                    if (totalBytes > 0)
                    {
                        var progress = (double)downloadedBytes / totalBytes * 100;

                        OnDownloadProgress?.Invoke(this, new UpdateProgressEventArgs
                        {
                            DownloadedBytes = downloadedBytes,
                            TotalBytes = totalBytes,
                            ProgressPercent = progress
                        });
                    }
                }

                _availableUpdate.LocalFilePath = filePath;

                Log.Information("[AutoUpdate] İndirme tamamlandı: {Path}", filePath);

                OnUpdateReady?.Invoke(this, new UpdateReadyEventArgs
                {
                    UpdateInfo = _availableUpdate,
                    InstallerPath = filePath
                });

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AutoUpdate] İndirme başarısız");

                OnUpdateError?.Invoke(this, new UpdateErrorEventArgs
                {
                    Error = ex,
                    Phase = UpdatePhase.Downloading
                });

                return false;
            }
            finally
            {
                _isDownloading = false;
            }
        }

        /// <summary>
        /// Güncellemeyi yükle ve uygulamayı yeniden başlat
        /// </summary>
        public bool InstallUpdate()
        {
            if (_availableUpdate?.LocalFilePath == null)
            {
                Log.Warning("[AutoUpdate] Yüklenecek dosya yok");
                return false;
            }

            if (!File.Exists(_availableUpdate.LocalFilePath))
            {
                Log.Error("[AutoUpdate] Installer dosyası bulunamadı: {Path}", _availableUpdate.LocalFilePath);
                return false;
            }

            try
            {
                Log.Information("[AutoUpdate] Güncelleme başlatılıyor...");

                // Installer'ı başlat
                var psi = new ProcessStartInfo
                {
                    FileName = _availableUpdate.LocalFilePath,
                    Arguments = "/SILENT /RESTARTAPPLICATIONS",
                    UseShellExecute = true
                };

                Process.Start(psi);

                // Uygulamayı kapat
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    System.Windows.Application.Current.Shutdown();
                });

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AutoUpdate] Güncelleme yükleme başarısız");

                OnUpdateError?.Invoke(this, new UpdateErrorEventArgs
                {
                    Error = ex,
                    Phase = UpdatePhase.Installing
                });

                return false;
            }
        }

        /// <summary>
        /// Güncellemeyi ertele
        /// </summary>
        public void DismissUpdate()
        {
            _availableUpdate = null;
            Log.Debug("[AutoUpdate] Güncelleme ertelendi");
        }

        /// <summary>
        /// Eski güncelleme dosyalarını temizle
        /// </summary>
        public void CleanupOldUpdates()
        {
            try
            {
                var files = Directory.GetFiles(_updateFolder, "*.exe");
                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.CreationTime < DateTime.Now.AddDays(-7))
                        {
                            File.Delete(file);
                            Log.Debug("[AutoUpdate] Eski dosya silindi: {File}", file);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[AutoUpdate] Temizlik hatası");
            }
        }

        #endregion

        #region Private Methods

        private async Task<UpdateInfo?> FetchUpdateInfoAsync(CancellationToken ct)
        {
            try
            {
                // GitHub Releases API
                var response = await _httpClient.GetStringAsync(Config.UpdateServerUrl, ct);
                var json = JsonDocument.Parse(response);
                var root = json.RootElement;

                var tagName = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
                var body = root.GetProperty("body").GetString() ?? "";
                var publishedAt = root.GetProperty("published_at").GetDateTime();

                // Asset bul (Windows installer)
                string? downloadUrl = null;
                long fileSize = 0;

                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString();
                            fileSize = asset.GetProperty("size").GetInt64();
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(downloadUrl))
                {
                    Log.Warning("[AutoUpdate] İndirme linki bulunamadı");
                    return null;
                }

                return new UpdateInfo
                {
                    Version = tagName,
                    ReleaseNotes = body,
                    DownloadUrl = downloadUrl,
                    FileSize = fileSize,
                    PublishedAt = publishedAt
                };
            }
            catch (HttpRequestException)
            {
                // Backup URL dene
                try
                {
                    var response = await _httpClient.GetStringAsync(Config.BackupUpdateUrl, ct);
                    return JsonSerializer.Deserialize<UpdateInfo>(response);
                }
                catch
                {
                    throw;
                }
            }
        }

        private bool IsNewerVersion(string newVersion)
        {
            try
            {
                var current = Version.Parse(_currentVersion.Split('-')[0]);
                var available = Version.Parse(newVersion.Split('-')[0]);

                return available > current;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _checkTimer.Dispose();
            _httpClient.Dispose();

            OnUpdateAvailable = null;
            OnDownloadProgress = null;
            OnUpdateReady = null;
            OnUpdateError = null;
        }

        #endregion
    }

    #region Types

    public class UpdateInfo
    {
        public string Version { get; init; } = "";
        public string ReleaseNotes { get; init; } = "";
        public string DownloadUrl { get; init; } = "";
        public long FileSize { get; init; }
        public DateTime PublishedAt { get; init; }
        public string? LocalFilePath { get; set; }

        public string FileSizeDisplay => FileSize switch
        {
            < 1024 => $"{FileSize} B",
            < 1024 * 1024 => $"{FileSize / 1024.0:F1} KB",
            _ => $"{FileSize / (1024.0 * 1024):F1} MB"
        };
    }

    public enum UpdatePhase
    {
        Checking,
        Downloading,
        Installing
    }

    public class UpdateAvailableEventArgs : EventArgs
    {
        public UpdateInfo UpdateInfo { get; init; } = null!;
    }

    public class UpdateProgressEventArgs : EventArgs
    {
        public long DownloadedBytes { get; init; }
        public long TotalBytes { get; init; }
        public double ProgressPercent { get; init; }
    }

    public class UpdateReadyEventArgs : EventArgs
    {
        public UpdateInfo UpdateInfo { get; init; } = null!;
        public string InstallerPath { get; init; } = "";
    }

    public class UpdateErrorEventArgs : EventArgs
    {
        public Exception Error { get; init; } = null!;
        public UpdatePhase Phase { get; init; }
    }

    #endregion
}
