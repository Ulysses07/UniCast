using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniCast.App.Services.Capture;
using UniCast.Core.Core;
using UniCast.Core.Models;
using UniCast.Core.Settings;
using UniCast.Core.Streaming;
using UniCast.Encoder;

namespace UniCast.App.Services
{
    /// <summary>
    /// FFmpeg tabanlı yayın yöneticisi.
    /// Çoklu platform desteği, otomatik encoder fallback ve graceful shutdown sağlar.
    /// </summary>
    public sealed class StreamController : IStreamController, IAsyncDisposable, IDisposable
    {
        private readonly SemaphoreSlim _stateLock = new(1, 1);
        private bool _isRunning;
        private bool _isReconnecting;

        public bool IsRunning
        {
            get
            {
                _stateLock.Wait();
                try { return _isRunning; }
                finally { _stateLock.Release(); }
            }
        }

        public bool IsReconnecting
        {
            get
            {
                _stateLock.Wait();
                try { return _isReconnecting; }
                finally { _stateLock.Release(); }
            }
        }

        public string? LastAdvisory { get; private set; }
        public string? LastMessage { get; private set; }
        public string? LastMetric { get; private set; }
        public IReadOnlyList<StreamTarget> Targets => _targets;
        public Profile CurrentProfile { get; private set; } = Profile.Default();

        public event EventHandler<string>? OnLog;
        public event EventHandler<StreamMetric>? OnMetric;
        public event EventHandler<int>? OnExit;

        private readonly List<StreamTarget> _targets = new();
        private FfmpegProcess? _ffmpegProcess;
        private CancellationTokenSource? _procCts;
        private bool _disposed;

        public void AddTarget(StreamTarget target)
        {
            if (target == null) return;
            lock (_targets)
            {
                _targets.Add(target);
            }
            OnLog?.Invoke(this, $"[targets] Eklendi: {target.DisplayName ?? target.Platform.ToString()}");
        }

        public void RemoveTarget(StreamTarget target)
        {
            if (target == null) return;
            lock (_targets)
            {
                _targets.Remove(target);
            }
            OnLog?.Invoke(this, $"[targets] Çıkarıldı: {target.DisplayName ?? target.Platform.ToString()}");
        }

        public async Task StartAsync(Profile profile, CancellationToken ct = default)
        {
            var result = await StartWithResultAsync(profile, ct);
            if (!result.Success) throw new InvalidOperationException(result.UserMessage ?? "Yayın başlatılamadı.");
        }

        public Task<StreamStartResult> StartWithResultAsync(Profile profile, CancellationToken ct = default)
            => StartInternalAsync(profile, null, null, false, ct);

        public Task<StreamStartResult> StartWithResultAsync(Profile profile, string? videoDevice, string? audioDevice, bool screenCapture, CancellationToken ct = default)
            => StartInternalAsync(profile, videoDevice, audioDevice, screenCapture, ct);

        public async Task<StreamStartResult> StartWithResultAsync(IEnumerable<TargetItem> items, SettingsData settings, CancellationToken ct)
        {
            var mapped = items.ToStreamTargets();

            lock (_targets)
            {
                _targets.Clear();
                _targets.AddRange(mapped);
            }

            var screenCapture = settings?.CaptureSource == CaptureSource.Screen;
            var videoDevice = settings?.CaptureSource == CaptureSource.Camera ? settings?.SelectedVideoDevice : null;
            var audioDevice = settings?.SelectedAudioDevice;

            var profile = settings?.GetSelectedProfile() ?? Profile.Default();

            if (settings != null)
            {
                profile.Fps = settings.Fps > 0 ? settings.Fps : Constants.Preview.DefaultFps;
                profile.Width = settings.Width > 0 ? settings.Width : Constants.Preview.DefaultWidth;
                profile.Height = settings.Height > 0 ? settings.Height : Constants.Preview.DefaultHeight;
                profile.VideoBitrateKbps = settings.VideoKbps > 0 ? settings.VideoKbps : Constants.StreamQuality.DefaultVideoBitrateKbps;
                profile.AudioBitrateKbps = settings.AudioKbps > 0 ? settings.AudioKbps : Constants.StreamQuality.DefaultAudioBitrateKbps;
                profile.AudioDelayMs = settings.AudioDelayMs;
            }

            return await StartInternalAsync(profile, videoDevice, audioDevice, screenCapture, ct);
        }

        async Task IStreamController.StartAsync(Profile profile, IEnumerable<StreamTarget> targets, CancellationToken ct)
        {
            lock (_targets)
            {
                _targets.Clear();
                if (targets != null) _targets.AddRange(targets);
            }
            var res = await StartWithResultAsync(profile, ct);
            if (!res.Success) throw new InvalidOperationException(res.UserMessage);
        }

        async Task IStreamController.StartAsync(IEnumerable<TargetItem> items, SettingsData settings, CancellationToken ct)
        {
            var res = await StartWithResultAsync(items, settings, ct);
            if (!res.Success) throw new InvalidOperationException(res.UserMessage);
        }

        public async Task StopAsync(CancellationToken ct = default)
        {
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_ffmpegProcess == null)
                {
                    OnLog?.Invoke(this, "[stop] Aktif yayın süreci yok.");
                    return;
                }

                try
                {
                    OnLog?.Invoke(this, "[stop] Yayın durduruluyor...");
                    await _ffmpegProcess.StopAsync();
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke(this, $"[stop][error] {ex.Message}");
                }
                finally
                {
                    CleanupProcess();
                }
            }
            finally
            {
                _stateLock.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Async işlemleri senkron olarak bekle
            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch { }

            _stateLock.Dispose();

            try { _procCts?.Cancel(); } catch { }
            _procCts?.Dispose();

            // Event'leri temizle
            OnLog = null;
            OnMetric = null;
            OnExit = null;

            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await StopAsync();

            _stateLock.Dispose();
            _procCts?.Dispose();

            // Event'leri temizle
            OnLog = null;
            OnMetric = null;
            OnExit = null;

            GC.SuppressFinalize(this);
        }

        private async Task<StreamStartResult> StartInternalAsync(
            Profile profile,
            string? videoDeviceId,
            string? audioDeviceId,
            bool screenCapture,
            CancellationToken ct)
        {
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_isRunning)
                {
                    return StreamStartResult.Fail(StreamErrorCode.InvalidConfig, "Yayın zaten çalışıyor.");
                }

                CurrentProfile = profile ?? Profile.Default();

                List<StreamTarget> enabledTargets;
                lock (_targets)
                {
                    enabledTargets = _targets
                        .Where(t => t?.Enabled == true && !string.IsNullOrWhiteSpace(t.Url))
                        .ToList();
                }

                if (enabledTargets.Count == 0)
                {
                    return StreamStartResult.Fail(StreamErrorCode.InvalidConfig,
                        "Etkin yayın hedefi yok. Lütfen en az bir hedef (RTMP) ekleyin.");
                }

                var deviceService = new DeviceService();
                string? finalVideoName = videoDeviceId;
                string? finalAudioName = audioDeviceId;

                if (!string.IsNullOrEmpty(videoDeviceId) && !screenCapture)
                {
                    var name = await deviceService.GetDeviceNameByIdAsync(videoDeviceId);
                    if (!string.IsNullOrEmpty(name)) finalVideoName = name;
                }

                if (!string.IsNullOrEmpty(audioDeviceId))
                {
                    var name = await deviceService.GetDeviceNameByIdAsync(audioDeviceId);
                    if (!string.IsNullOrEmpty(name)) finalAudioName = name;
                }

                var globalSettings = SettingsStore.Load();
                int delayMs = CurrentProfile.AudioDelayMs > 0 ? CurrentProfile.AudioDelayMs : globalSettings.AudioDelayMs;

                string? recordPath = null;
                if (globalSettings.EnableLocalRecord && !string.IsNullOrWhiteSpace(globalSettings.RecordFolder))
                {
                    try
                    {
                        if (!Directory.Exists(globalSettings.RecordFolder))
                            Directory.CreateDirectory(globalSettings.RecordFolder);

                        string fileName = $"UniCast_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
                        recordPath = Path.Combine(globalSettings.RecordFolder, fileName);

                        OnLog?.Invoke(this, $"[record] Kayıt aktif: {fileName}");
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke(this, $"[record] Hata: {ex.Message}");
                        recordPath = null;
                    }
                }

                // DÜZELTME: Constants kullanımı
                var overlayPipeName = globalSettings.ShowOverlay ? Constants.Overlay.PipeName : null;

                var args = FfmpegArgsBuilder.BuildFfmpegArgs(
                    CurrentProfile,
                    enabledTargets,
                    finalVideoName,
                    finalAudioName,
                    screenCapture,
                    delayMs,
                    recordPath,
                    overlayPipeName,
                    globalSettings.Encoder
                );

                _procCts?.Cancel();
                _procCts?.Dispose();
                _procCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                _ffmpegProcess = new FfmpegProcess();

                _ffmpegProcess.OnLog += (line) =>
                {
                    LastMessage = line;
                    OnLog?.Invoke(this, line);
                    if (line.Contains("Connection") || line.Contains("Reconnect"))
                        LastAdvisory = line;
                };

                _ffmpegProcess.OnMetric += (line) =>
                {
                    var metric = TryParseMetric(line, DateTime.UtcNow);
                    if (metric != null)
                    {
                        LastMetric = $"fps={metric.Fps:0.#} bitrate={metric.BitrateKbps:0.#}k";
                        OnMetric?.Invoke(this, metric);
                    }
                };

                _ffmpegProcess.OnExit += (code) =>
                {
                    _isRunning = false;
                    OnExit?.Invoke(this, code ?? -1);
                    OnLog?.Invoke(this, $"[ffmpeg] Çıkış kodu: {code}");
                };

                try
                {
                    await _ffmpegProcess.StartAsync(args, _procCts.Token);
                    _isRunning = true;
                    OnLog?.Invoke(this, $"[start] Yayın başladı. Profil: {CurrentProfile.Name} Encoder: {globalSettings.Encoder}");
                    return StreamStartResult.Ok();
                }
                catch (Exception ex)
                {
                    CleanupProcess();

                    var msg = ex.Message.ToLowerInvariant();

                    if ((msg.Contains("encoder") || msg.Contains("codec") || msg.Contains("device")) &&
                       !globalSettings.Encoder.Contains("libx264") &&
                       (globalSettings.Encoder != "auto"))
                    {
                        OnLog?.Invoke(this, $"[warn] Seçilen encoder ({globalSettings.Encoder}) başarısız oldu. CPU (libx264) ile tekrar deneniyor...");

                        var cpuArgs = FfmpegArgsBuilder.BuildFfmpegArgs(
                            CurrentProfile, enabledTargets, finalVideoName, finalAudioName,
                            screenCapture, delayMs, recordPath, overlayPipeName, "libx264");

                        try
                        {
                            _procCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            _ffmpegProcess = new FfmpegProcess();

                            _ffmpegProcess.OnLog += (l) => { LastMessage = l; OnLog?.Invoke(this, l); };
                            _ffmpegProcess.OnMetric += (line) =>
                            {
                                var metric = TryParseMetric(line, DateTime.UtcNow);
                                if (metric != null)
                                {
                                    LastMetric = $"fps={metric.Fps:0.#} bitrate={metric.BitrateKbps:0.#}k";
                                    OnMetric?.Invoke(this, metric);
                                }
                            };
                            _ffmpegProcess.OnExit += (c) => { _isRunning = false; OnExit?.Invoke(this, c ?? -1); };

                            await _ffmpegProcess.StartAsync(cpuArgs, _procCts.Token);
                            _isRunning = true;
                            return StreamStartResult.Ok();
                        }
                        catch
                        {
                            CleanupProcess();
                        }
                    }

                    if (msg.Contains("camera") || msg.Contains("video device") || msg.Contains("busy"))
                        return StreamStartResult.Fail(StreamErrorCode.CameraBusy, "Kamera meşgul.", ex.Message);

                    return StreamStartResult.Fail(StreamErrorCode.Unknown, $"Hata: {ex.Message}", ex.Message);
                }
            }
            finally
            {
                _stateLock.Release();
            }
        }

        private void CleanupProcess()
        {
            _isRunning = false;
            _ffmpegProcess?.Dispose();
            _ffmpegProcess = null;

            try { _procCts?.Cancel(); } catch { }
            _procCts?.Dispose();
            _procCts = null;
        }

        private static StreamMetric TryParseMetric(string line, DateTime tsUtc)
        {
            if (string.IsNullOrWhiteSpace(line)) return new StreamMetric { TimestampUtc = tsUtc };
            double? fps = null;
            double? br = null;
            try
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var p in parts)
                {
                    if (p.StartsWith("fps=", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = p.Substring(4);
                        if (double.TryParse(v.Replace(",", "."), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var f)) fps = f;
                    }
                    else if (p.StartsWith("bitrate=", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = p.Substring(8).ToLowerInvariant();
                        if (v.EndsWith("kbits/s") && double.TryParse(v.Replace("kbits/s", "").Trim().Replace(",", "."),
                            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var kb)) br = kb;
                    }
                }
            }
            catch { }
            return new StreamMetric { TimestampUtc = tsUtc, Fps = fps, BitrateKbps = br };
        }
    }
}