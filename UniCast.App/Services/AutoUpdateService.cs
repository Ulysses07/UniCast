using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Diagnostics;
using System.IO;
using Serilog;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace UniCast.App.Services
{
    /// <summary>
    /// Auto Update Service - Uygulama güncellemelerini kontrol eder ve yükler.
    /// unicastapp.com'dan güncelleme bilgisi çeker.
    /// </summary>
    public sealed class AutoUpdateService : IDisposable
    {
        #region Singleton

        private static readonly Lazy<AutoUpdateService> _instance =
            new(() => new AutoUpdateService(), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        public static AutoUpdateService Instance => _instance.Value;

        #endregion

        #region Fields

        private readonly HttpClient _httpClient;
        private bool _disposed;

        #endregion

        #region Properties

        /// <summary>
        /// Güncelleme bilgisi URL'i
        /// </summary>
        public string UpdateUrl { get; set; } = "https://unicastapp.com/downloads/update.json";

        /// <summary>
        /// Mevcut versiyon
        /// </summary>
        public Version CurrentVersion { get; }

        /// <summary>
        /// Son kontrol zamanı
        /// </summary>
        public DateTime? LastCheckTime { get; private set; }

        /// <summary>
        /// Güncelleme mevcut mu
        /// </summary>
        public bool UpdateAvailable { get; private set; }

        /// <summary>
        /// Yeni versiyon bilgisi
        /// </summary>
        public UpdateInfo? LatestUpdate { get; private set; }

        #endregion

        #region Events

        /// <summary>
        /// Güncelleme bulunduğunda tetiklenir
        /// </summary>
        public event EventHandler<UpdateInfo>? UpdateFound;

        /// <summary>
        /// İndirme ilerlemesi
        /// </summary>
        public event EventHandler<int>? DownloadProgress;

        #endregion

        #region Constructor

        private AutoUpdateService()
        {
            CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", $"UniCast/{CurrentVersion}");

            Log.Debug("[AutoUpdate] Service initialized. Current version: {Version}", CurrentVersion);
        }

        #endregion

        #region Public API

        /// <summary>
        /// Güncelleme kontrolü yapar
        /// </summary>
        public async Task<bool> CheckForUpdatesAsync(bool silent = true)
        {
            try
            {
                Log.Debug("[AutoUpdate] Checking for updates...");

                var response = await _httpClient.GetAsync(UpdateUrl).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("[AutoUpdate] Server returned {StatusCode}", response.StatusCode);
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var updateInfo = JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (updateInfo == null)
                {
                    Log.Warning("[AutoUpdate] Invalid update info");
                    return false;
                }

                LastCheckTime = DateTime.Now;
                var latestVersion = Version.Parse(updateInfo.Version);

                if (latestVersion > CurrentVersion)
                {
                    UpdateAvailable = true;
                    LatestUpdate = updateInfo;

                    Log.Information("[AutoUpdate] New version available: {Version} (current: {Current})",
                        updateInfo.Version, CurrentVersion);

                    // Event tetikle
                    UpdateFound?.Invoke(this, updateInfo);

                    // Kullanıcıya sor (silent değilse)
                    if (!silent)
                    {
                        await ShowUpdateDialogAsync(updateInfo);
                    }

                    return true;
                }
                else
                {
                    Log.Debug("[AutoUpdate] Already up to date. Current: {Current}, Latest: {Latest}",
                        CurrentVersion, latestVersion);
                    UpdateAvailable = false;
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[AutoUpdate] Update check failed");
                return false;
            }
        }

        /// <summary>
        /// Güncellemeyi indirir ve başlatır
        /// </summary>
        public async Task<bool> DownloadAndInstallAsync()
        {
            if (LatestUpdate == null || string.IsNullOrEmpty(LatestUpdate.DownloadUrl))
            {
                Log.Warning("[AutoUpdate] No update info available");
                return false;
            }

            try
            {
                Log.Information("[AutoUpdate] Downloading update from {Url}", LatestUpdate.DownloadUrl);

                // Temp dosyaya indir
                var tempPath = Path.Combine(Path.GetTempPath(), $"UniCast-Setup-{LatestUpdate.Version}.exe");

                using (var response = await _httpClient.GetAsync(LatestUpdate.DownloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    var downloadedBytes = 0L;

                    using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                    var buffer = new byte[8192];
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
                        downloadedBytes += bytesRead;

                        if (totalBytes > 0)
                        {
                            var progress = (int)((downloadedBytes * 100) / totalBytes);
                            DownloadProgress?.Invoke(this, progress);
                        }
                    }
                }

                Log.Information("[AutoUpdate] Download complete: {Path}", tempPath);

                // SHA256 kontrolü (varsa)
                if (!string.IsNullOrEmpty(LatestUpdate.Sha256))
                {
                    var hash = await ComputeFileHashAsync(tempPath).ConfigureAwait(false);
                    if (!string.Equals(hash, LatestUpdate.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Error("[AutoUpdate] Hash mismatch! Expected: {Expected}, Got: {Actual}",
                            LatestUpdate.Sha256, hash);
                        File.Delete(tempPath);
                        return false;
                    }
                    Log.Debug("[AutoUpdate] Hash verified");
                }

                // Installer'ı başlat ve uygulamayı kapat
                Log.Information("[AutoUpdate] Starting installer...");

                Process.Start(new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "/SILENT", // Inno Setup silent install
                    UseShellExecute = true
                });

                // Uygulamayı kapat
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    System.Windows.Application.Current.Shutdown();
                });

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AutoUpdate] Download/install failed");
                return false;
            }
        }

        /// <summary>
        /// Güncellemeyi atla (bu versiyon için)
        /// </summary>
        public void SkipVersion()
        {
            if (LatestUpdate != null)
            {
                // Settings'e kaydet - bu versiyonu atla
                Log.Information("[AutoUpdate] User skipped version {Version}", LatestUpdate.Version);
            }
        }

        #endregion

        #region Private Methods

        private async Task ShowUpdateDialogAsync(UpdateInfo updateInfo)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var message = $"Yeni sürüm mevcut!\n\n" +
                              $"Mevcut: {CurrentVersion}\n" +
                              $"Yeni: {updateInfo.Version}\n\n" +
                              $"{updateInfo.ReleaseNotes ?? ""}\n\n" +
                              $"Şimdi güncellemek ister misiniz?";

                var result = MessageBox.Show(
                    message,
                    "UniCast - Güncelleme",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Information);

                switch (result)
                {
                    case MessageBoxResult.Yes:
                        _ = DownloadAndInstallAsync();
                        break;
                    case MessageBoxResult.No:
                        SkipVersion();
                        break;
                    // Cancel = sonra hatırlat
                }
            });
        }

        private static async Task<string> ComputeFileHashAsync(string filePath)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = await Task.Run(() => sha256.ComputeHash(stream)).ConfigureAwait(false);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _httpClient.Dispose();
            Log.Debug("[AutoUpdate] Service disposed");
        }

        #endregion
    }

    #region Models

    /// <summary>
    /// Güncelleme bilgisi modeli
    /// </summary>
    public class UpdateInfo
    {
        /// <summary>Yeni versiyon (örn: "1.2.0")</summary>
        public string Version { get; set; } = "";

        /// <summary>İndirme URL'i</summary>
        public string DownloadUrl { get; set; } = "";

        /// <summary>Dosya boyutu (bytes)</summary>
        public long FileSize { get; set; }

        /// <summary>SHA256 hash (doğrulama için)</summary>
        public string? Sha256 { get; set; }

        /// <summary>Yayın notları</summary>
        public string? ReleaseNotes { get; set; }

        /// <summary>Zorunlu güncelleme mi</summary>
        public bool Mandatory { get; set; }

        /// <summary>Minimum desteklenen versiyon</summary>
        public string? MinimumVersion { get; set; }

        /// <summary>Yayın tarihi</summary>
        public DateTime ReleaseDate { get; set; }
    }

    #endregion
}
