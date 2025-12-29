using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using UniCast.Core.Core;
using UniCast.Core.Models;
using UniCast.Core.Services;
using UniCast.Core.Streaming;

// App.Services.SettingsData kullanılacak
using SettingsData = UniCast.App.Services.SettingsData;

namespace UniCast.App.Services
{
    /// <summary>
    /// IStreamController interface'ini implemente eden adapter.
    /// StreamController (UniCast.Core.Services) ile IStreamController arasında köprü görevi görür.
    /// </summary>
    public sealed class StreamControllerAdapter : IStreamController
    {
        private readonly StreamController _inner;
        private readonly List<StreamTarget> _targets = new();
        private Profile _currentProfile = Profile.Default();

        private bool _isReconnecting;
        private string? _lastAdvisory;
        private string? _lastMessage;
        private string? _lastMetric;

        public StreamControllerAdapter()
        {
            _inner = StreamController.Instance;

            // Event'leri bağla
            _inner.LogMessage += OnLogMessage;
            _inner.StateChanged += OnStateChanged;
            _inner.StatisticsUpdated += OnStatisticsUpdated;
        }

        #region IStreamController Properties

        public bool IsRunning => _inner.IsRunning;

        public bool IsReconnecting => _isReconnecting;

        public Profile CurrentProfile => _currentProfile;

        public IReadOnlyList<StreamTarget> Targets => _targets.AsReadOnly();

        public string? LastAdvisory => _lastAdvisory;

        public string? LastMessage => _lastMessage;

        public string? LastMetric => _lastMetric;

        #endregion

        #region IStreamController Events

        public event EventHandler<string>? OnLog;
        public event EventHandler<StreamMetric>? OnMetric;
        public event EventHandler<int>? OnExit;

        #endregion

        #region IStreamController Methods

        public async Task<StreamStartResult> StartWithResultAsync(
            IEnumerable<TargetItem> targets,
            SettingsData settings,
            CancellationToken ct)
        {
            try
            {
                // DEBUG: Gelen hedefleri logla
                var targetList = targets.ToList();
                Log.Debug("[StreamControllerAdapter] Gelen hedef sayısı: {Count}", targetList.Count);
                foreach (var t in targetList)
                {
                    Log.Debug("[StreamControllerAdapter] Hedef: Platform={Platform}, Url={Url}, Enabled={Enabled}",
                        t.Platform, t.Url ?? "(null)", t.Enabled);
                }

                // TargetItem'ları StreamTarget'a dönüştür
                var streamTargets = targetList
                    .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Url))
                    .Select(t => new StreamTarget
                    {
                        Platform = t.Platform,
                        DisplayName = t.DisplayName,
                        Url = t.Url,
                        StreamKey = t.StreamKey,
                        Enabled = t.Enabled
                    })
                    .ToList();

                Log.Debug("[StreamControllerAdapter] Filtrelenmiş hedef sayısı: {Count}", streamTargets.Count);

                if (!streamTargets.Any())
                {
                    return StreamStartResult.Fail(
                        StreamErrorCode.InvalidConfig,
                        "Aktif hedef bulunamadı. En az bir yayın hedefi ekleyin ve etkinleştirin.");
                }

                // DÜZELTME: Hedefleri _targets'a kaydet
                _targets.Clear();
                _targets.AddRange(streamTargets);

                // Profil oluştur
                var profile = new Profile
                {
                    Name = "Current",
                    Width = settings.Width,
                    Height = settings.Height,
                    Fps = settings.Fps,
                    VideoBitrateKbps = settings.VideoKbps,
                    AudioBitrateKbps = settings.AudioKbps,
                    VideoPreset = "veryfast",
                    AudioCodec = settings.AudioEncoder ?? "aac",
                    VideoCodec = settings.VideoEncoder ?? "libx264",
                    AudioDelayMs = settings.AudioDelayMs
                };

                return await StartWithResultAsync(profile, ct);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[StreamControllerAdapter] StartWithResultAsync hatası");
                return StreamStartResult.Fail(
                    StreamErrorCode.Unknown,
                    $"Beklenmeyen hata: {ex.Message}",
                    ex.ToString());
            }
        }

        public async Task<StreamStartResult> StartWithResultAsync(Profile profile, CancellationToken ct = default)
        {
            try
            {
                _currentProfile = profile;
                _lastAdvisory = null;
                _lastMessage = "Başlatılıyor...";
                _lastMetric = null;

                // Aktif hedefleri al
                var activeTargets = _targets.Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Url)).ToList();

                if (!activeTargets.Any())
                {
                    return StreamStartResult.Fail(
                        StreamErrorCode.InvalidConfig,
                        "Geçerli yayın hedefi bulunamadı.");
                }

                // Multi-target için output URL'leri birleştir
                string outputUrl;
                if (activeTargets.Count == 1)
                {
                    // Tek hedef - normal output
                    outputUrl = BuildOutputUrl(activeTargets[0]);
                }
                else
                {
                    // Çoklu hedef - tee muxer kullan
                    outputUrl = BuildMultiTargetOutput(activeTargets);
                    Log.Information("[StreamControllerAdapter] Multi-target: {Count} platforma yayın yapılacak", activeTargets.Count);
                }

                // StreamConfiguration oluştur
                var config = new StreamConfiguration
                {
                    InputSource = GetInputSource(),
                    AudioSource = GetAudioSource(),
                    OutputUrl = outputUrl,
                    VideoBitrate = profile.VideoBitrateKbps,
                    AudioBitrate = profile.AudioBitrateKbps,
                    Fps = profile.Fps,
                    Preset = profile.VideoPreset,
                    UseTeeMuxer = activeTargets.Count > 1,  // Tee muxer flag
                    CameraRotation = SettingsStore.Data.CameraRotation  // Kamera döndürme
                };

                var success = await _inner.StartAsync(config, ct);

                if (success)
                {
                    _lastMessage = "Yayın başladı";
                    return StreamStartResult.Ok();
                }
                else
                {
                    _lastAdvisory = "FFmpeg başlatılamadı";
                    return StreamStartResult.Fail(
                        StreamErrorCode.ProcessFailed,
                        "Yayın başlatılamadı. FFmpeg çalıştırılamadı.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[StreamControllerAdapter] StartWithResultAsync hatası");
                _lastAdvisory = ex.Message;
                return StreamStartResult.Fail(
                    StreamErrorCode.Unknown,
                    $"Yayın başlatma hatası: {ex.Message}",
                    ex.ToString());
            }
        }

        /// <summary>
        /// Çoklu hedef için tee muxer output string'i oluşturur
        /// </summary>
        private string BuildMultiTargetOutput(List<StreamTarget> targets)
        {
            // FFmpeg tee muxer formatı:
            // "tee:[f=flv:onfail=ignore]rtmp://url1|[f=flv:onfail=ignore]rtmp://url2"
            var outputs = new List<string>();

            foreach (var target in targets)
            {
                var url = BuildOutputUrl(target);

                // Facebook rtmps için özel handling
                if (url.StartsWith("rtmps://"))
                {
                    // RTMPS için flvflags ekle
                    outputs.Add($"[f=flv:flvflags=no_duration_filesize:onfail=ignore]{url}");
                }
                else
                {
                    outputs.Add($"[f=flv:flvflags=no_duration_filesize:onfail=ignore]{url}");
                }

                Log.Debug("[StreamControllerAdapter] Tee output eklendi: {Platform} -> {Url}",
                    target.Platform, MaskStreamKey(url));
            }

            // tee: prefix'i ile birleştir
            return "tee:" + string.Join("|", outputs);
        }

        /// <summary>
        /// Stream key'i maskeler (güvenlik için)
        /// </summary>
        private string MaskStreamKey(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;

            // Son 20 karakteri maskele
            var uri = url;
            if (uri.Length > 30)
            {
                var lastSlash = uri.LastIndexOf('/');
                if (lastSlash > 0 && lastSlash < uri.Length - 10)
                {
                    var key = uri.Substring(lastSlash + 1);
                    if (key.Length > 8)
                    {
                        return uri.Substring(0, lastSlash + 1) + key.Substring(0, 4) + "********";
                    }
                }
            }
            return uri;
        }

        public async Task StartAsync(Profile profile, CancellationToken ct = default)
        {
            await StartWithResultAsync(profile, ct);
        }

        public async Task StartAsync(Profile profile, IEnumerable<StreamTarget> targets, CancellationToken ct)
        {
            _targets.Clear();
            _targets.AddRange(targets);
            await StartWithResultAsync(profile, ct);
        }

        public async Task StartAsync(IEnumerable<TargetItem> targets, SettingsData settings, CancellationToken ct)
        {
            // TargetItem'ları StreamTarget'a dönüştür
            _targets.Clear();
            _targets.AddRange(targets
                .Where(t => t.Enabled)
                .Select(t => new StreamTarget
                {
                    Platform = t.Platform,
                    DisplayName = t.DisplayName,
                    Url = t.Url,
                    StreamKey = t.StreamKey,
                    Enabled = t.Enabled
                }));

            await StartWithResultAsync(targets, settings, ct);
        }

        public async Task StopAsync(CancellationToken ct = default)
        {
            try
            {
                _lastMessage = "Durduruluyor...";
                await _inner.StopAsync();
                _lastMessage = "Durduruldu";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[StreamControllerAdapter] StopAsync hatası");
                _lastAdvisory = ex.Message;
            }
        }

        public void AddTarget(StreamTarget target)
        {
            if (target != null && !_targets.Contains(target))
            {
                _targets.Add(target);
            }
        }

        public void RemoveTarget(StreamTarget target)
        {
            _targets.Remove(target);
        }

        #endregion

        #region Private Methods

        private string GetInputSource()
        {
            var settings = SettingsStore.Data;

            // SelectedCamera artık direkt cihaz adı olarak kaydediliyor
            var deviceName = settings.SelectedCamera;

            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                // Eğer cihaz değeri zaten "video=" ile başlıyorsa olduğu gibi kullan
                if (deviceName.StartsWith("video=", StringComparison.OrdinalIgnoreCase))
                {
                    return deviceName;
                }

                // Eski format (ID) kontrolü - geriye uyumluluk için
                if (deviceName.Contains("\\\\?\\") || deviceName.Contains(@"\\?\") ||
                    deviceName.Contains("{") || deviceName.Contains("ROOT#MEDIA") ||
                    deviceName.Contains("#GLOBAL") || deviceName.Contains("ROOT#"))
                {
                    Log.Warning("[StreamControllerAdapter] Eski format ID tespit edildi: {Device}. " +
                        "Lütfen Ayarlar'dan kamerayı yeniden seçin.", deviceName);

                    // Eski ID formatı - ilk kamerayı kullan
                    try
                    {
                        var deviceService = new UniCast.App.Services.Capture.DeviceService();
                        var devices = Task.Run(async () => await deviceService.GetVideoDevicesAsync())
                            .GetAwaiter().GetResult();

                        if (devices.Count > 0)
                        {
                            var firstDevice = devices[0];
                            Log.Information("[StreamControllerAdapter] İlk kamera kullanılıyor: {Name}", firstDevice.Name);
                            return $"video={firstDevice.Name}";
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[StreamControllerAdapter] Cihaz listesi alınamadı");
                    }

                    return "video=Integrated Camera";
                }

                // Normal cihaz adı - doğrudan kullan
                Log.Debug("[StreamControllerAdapter] Kamera: {Name}", deviceName);
                return $"video={deviceName}";
            }

            // Ayarlarda cihaz yok - ilk kamerayı bulmaya çalış
            Log.Warning("[StreamControllerAdapter] Kamera ayarlanmamış, ilk kamera aranıyor...");
            try
            {
                var deviceService = new UniCast.App.Services.Capture.DeviceService();
                var devices = Task.Run(async () => await deviceService.GetVideoDevicesAsync())
                    .GetAwaiter().GetResult();

                if (devices.Count > 0)
                {
                    var firstDevice = devices[0];
                    Log.Information("[StreamControllerAdapter] İlk kamera kullanılıyor: {Name}", firstDevice.Name);
                    return $"video={firstDevice.Name}";
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[StreamControllerAdapter] Varsayılan kamera bulunamadı");
            }

            // Son çare
            Log.Error("[StreamControllerAdapter] Hiçbir kamera bulunamadı!");
            return "video=Integrated Camera";
        }

        private string? GetAudioSource()
        {
            var settings = SettingsStore.Data;

            // SelectedMicrophone artık direkt cihaz adı olarak kaydediliyor
            var deviceName = settings.SelectedMicrophone;

            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                // Eski format (ID) kontrolü - geriye uyumluluk için
                if (deviceName.Contains("\\\\?\\") || deviceName.Contains(@"\\?\") ||
                    deviceName.Contains("{") || deviceName.Contains("ROOT#") ||
                    deviceName.Contains("#GLOBAL") || deviceName.Contains("SWD#") ||
                    deviceName.Contains("MMDEVAPI"))
                {
                    Log.Warning("[StreamControllerAdapter] Eski format mikrofon ID tespit edildi: {Device}. " +
                        "Lütfen Ayarlar'dan mikrofonu yeniden seçin.", deviceName);

                    // Eski ID formatı - ilk mikrofonu kullan
                    try
                    {
                        var deviceService = new UniCast.App.Services.Capture.DeviceService();
                        var devices = Task.Run(async () => await deviceService.GetAudioDevicesAsync())
                            .GetAwaiter().GetResult();

                        if (devices.Count > 0)
                        {
                            var firstDevice = devices[0];
                            Log.Information("[StreamControllerAdapter] İlk mikrofon kullanılıyor: {Name}", firstDevice.Name);
                            return firstDevice.Name;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[StreamControllerAdapter] Mikrofon listesi alınamadı");
                    }

                    return null;
                }

                // Normal cihaz adı
                Log.Debug("[StreamControllerAdapter] Mikrofon: {Name}", deviceName);
                return deviceName;
            }

            // Ayarlarda mikrofon yok - ilk mikrofonu bulmaya çalış
            Log.Warning("[StreamControllerAdapter] Mikrofon ayarlanmamış, ilk mikrofon aranıyor...");
            try
            {
                var deviceService = new UniCast.App.Services.Capture.DeviceService();
                var devices = Task.Run(async () => await deviceService.GetAudioDevicesAsync())
                    .GetAwaiter().GetResult();

                if (devices.Count > 0)
                {
                    var firstDevice = devices[0];
                    Log.Information("[StreamControllerAdapter] İlk mikrofon kullanılıyor: {Name}", firstDevice.Name);
                    return firstDevice.Name;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[StreamControllerAdapter] Varsayılan mikrofon bulunamadı");
            }

            // Mikrofon bulunamadı - sessiz audio kullanılacak
            Log.Warning("[StreamControllerAdapter] Hiçbir mikrofon bulunamadı, sessiz audio kullanılacak");
            return null;
        }

        private string BuildOutputUrl(StreamTarget target)
        {
            var url = target.Url ?? "";
            var streamKey = target.StreamKey ?? "";

            // Stream key boşsa direkt URL'i döndür
            if (string.IsNullOrWhiteSpace(streamKey))
            {
                return url;
            }

            // URL zaten stream key içeriyor mu kontrol et
            // Facebook gibi platformlar URL'in içinde key verir
            if (url.Contains(streamKey))
            {
                // Key zaten URL'de var, duplikasyon yapma
                return url;
            }

            // URL key ile bitiyorsa ekleme yapma
            if (url.EndsWith(streamKey))
            {
                return url;
            }

            // Key'i ekle
            if (!url.EndsWith("/"))
                url += "/";
            url += streamKey;

            return url;
        }

        private void OnLogMessage(string level, string message)
        {
            // FFmpeg Stats mesajlarını Status'a yazmayalım, sadece log'a gönderelim
            // Stats mesajları "[FFmpeg Stats]" ile başlar veya "frame=" içerir
            bool isStatsMessage = message.Contains("[FFmpeg Stats]") ||
                                  message.Contains("frame=") ||
                                  message.Contains("fps=") ||
                                  message.Contains("bitrate=") ||
                                  message.Contains("time=") ||
                                  message.StartsWith("[FFmpeg]");

            if (!isStatsMessage)
            {
                // Sadece durum mesajlarını göster
                _lastMessage = message;
            }

            OnLog?.Invoke(this, message);
        }

        private void OnStateChanged(object? sender, StreamStateChangedEventArgs e)
        {
            switch (e.NewState)
            {
                case StreamState.Starting:
                    _lastMessage = "Başlatılıyor...";
                    _isReconnecting = false;
                    break;
                case StreamState.Running:
                    _lastMessage = "Yayında";
                    _isReconnecting = false;
                    break;
                case StreamState.Stopping:
                    _lastMessage = "Durduruluyor...";
                    _isReconnecting = false;
                    break;
                case StreamState.Stopped:
                    _lastMessage = "Durduruldu";
                    _isReconnecting = false;
                    OnExit?.Invoke(this, 0); // Normal çıkış
                    break;
                case StreamState.Error:
                    _lastAdvisory = "Yayın hatası oluştu";
                    _isReconnecting = true; // Hata durumunda yeniden bağlanma denenebilir
                    OnExit?.Invoke(this, 1); // Hata ile çıkış
                    break;
            }
        }

        private void OnStatisticsUpdated(object? sender, StreamStatisticsEventArgs e)
        {
            var stats = e.Statistics;
            _lastMetric = $"FPS: {stats.CurrentFps:F1} | Bitrate: {stats.CurrentBitrate:F0} kbps";

            OnMetric?.Invoke(this, new StreamMetric
            {
                Fps = stats.CurrentFps,
                BitrateKbps = stats.CurrentBitrate,
                TimestampUtc = stats.Timestamp
            });
        }

        #endregion

        #region IAsyncDisposable

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_inner.IsRunning)
                {
                    await _inner.StopAsync();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[StreamControllerAdapter] DisposeAsync hatası");
            }

            // Event'leri temizle
            _inner.LogMessage -= OnLogMessage;
            _inner.StateChanged -= OnStateChanged;
            _inner.StatisticsUpdated -= OnStatisticsUpdated;

            OnLog = null;
            OnMetric = null;
            OnExit = null;
        }

        #endregion
    }
}