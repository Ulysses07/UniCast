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

                // İlk hedefi al
                var target = _targets.FirstOrDefault(t => t.Enabled);
                if (target == null || string.IsNullOrWhiteSpace(target.Url))
                {
                    return StreamStartResult.Fail(
                        StreamErrorCode.InvalidConfig,
                        "Geçerli yayın hedefi bulunamadı.");
                }

                // StreamConfiguration oluştur
                var config = new StreamConfiguration
                {
                    InputSource = GetInputSource(),
                    OutputUrl = BuildOutputUrl(target),
                    VideoBitrate = profile.VideoBitrateKbps,
                    AudioBitrate = profile.AudioBitrateKbps,
                    Fps = profile.Fps,
                    Preset = profile.VideoPreset
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

            // SelectedCamera'yı kullan (VideoDevice, SelectedVideoDevice bunun alias'ı)
            var deviceValue = settings.SelectedCamera;

            if (!string.IsNullOrWhiteSpace(deviceValue))
            {
                // Eğer cihaz değeri zaten "video=" ile başlıyorsa olduğu gibi kullan
                if (deviceValue.StartsWith("video=", StringComparison.OrdinalIgnoreCase))
                {
                    return deviceValue;
                }

                // Windows device path formatı (\\?\..., GUID, ROOT#MEDIA) ise
                // Bu ID formatı - FFmpeg için uygun değil
                if (deviceValue.Contains("\\\\?\\") || deviceValue.Contains(@"\\?\") ||
                    deviceValue.Contains("{") || deviceValue.Contains("ROOT#MEDIA") ||
                    deviceValue.Contains("#GLOBAL"))
                {
                    Log.Warning("[StreamControllerAdapter] Ayarlardaki kamera ID formatında: {Device}. " +
                        "FFmpeg için cihaz adı çözümlenecek.", deviceValue);

                    // DeviceService ile ID'den Name'e çevir
                    try
                    {
                        var deviceService = new UniCast.App.Services.Capture.DeviceService();
                        var nameTask = deviceService.GetDeviceNameByIdAsync(deviceValue);
                        nameTask.Wait(TimeSpan.FromSeconds(2)); // Timeout ile bekle

                        if (nameTask.IsCompletedSuccessfully && !string.IsNullOrEmpty(nameTask.Result))
                        {
                            var deviceName = nameTask.Result;
                            Log.Information("[StreamControllerAdapter] Cihaz adı çözümlendi: {Name}", deviceName);
                            return $"video={deviceName}";
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[StreamControllerAdapter] Cihaz adı çözümlenemedi");
                    }

                    // Çözümlenemezse varsayılana dön
                    return "video=0";
                }

                // Normal cihaz adı
                return $"video={deviceValue}";
            }

            // Varsayılan: indeks 0 (ilk kamera) - FFmpeg bunu destekler
            return "video=0";
        }

        private string BuildOutputUrl(StreamTarget target)
        {
            var url = target.Url ?? "";

            if (!string.IsNullOrWhiteSpace(target.StreamKey))
            {
                if (!url.EndsWith("/"))
                    url += "/";
                url += target.StreamKey;
            }

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