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
    public sealed class StreamController : IStreamController, IAsyncDisposable
    {
        // --- Properties ---
        public bool IsRunning { get; private set; }
        public bool IsReconnecting { get; private set; } = false;
        public string? LastAdvisory { get; private set; }
        public string? LastMessage { get; private set; }
        public string? LastMetric { get; private set; }
        public IReadOnlyList<StreamTarget> Targets => _targets;
        public Profile CurrentProfile { get; private set; } = Profile.Default();

        // --- Events ---
        public event EventHandler<string>? OnLog;
        public event EventHandler<StreamMetric>? OnMetric;
        public event EventHandler<int>? OnExit;

        // --- Fields ---
        private readonly List<StreamTarget> _targets = new();
        private FfmpegProcess? _ffmpegProcess;
        private CancellationTokenSource? _procCts;

        // ----------------- Public API -----------------

        public void AddTarget(StreamTarget target)
        {
            if (target == null) return;
            _targets.Add(target);
            OnLog?.Invoke(this, $"[targets] Eklendi: {target.DisplayName ?? target.Platform.ToString()}");
        }

        public void RemoveTarget(StreamTarget target)
        {
            if (target == null) return;
            _targets.Remove(target);
            OnLog?.Invoke(this, $"[targets] Çıkarıldı: {target.DisplayName ?? target.Platform.ToString()}");
        }

        // --- Interface Implementation (Legacy & New) ---

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
            // HATA DÜZELTME (CS0019):
            // 'items?.ToStreamTargets() ?? Array.Empty...' şeklindeki kullanım tür uyuşmazlığı yaratıyordu.
            // ToStreamTargets metodu zaten null kontrolü yapıp her zaman geçerli bir List döndürdüğü için sadeleştirdik.
            var mapped = items.ToStreamTargets();

            _targets.Clear();
            _targets.AddRange(mapped);

            var screenCapture = settings?.CaptureSource == CaptureSource.Screen;
            // SettingsData artık ID tutuyor, bunları Internal metoda paslıyoruz, o çözümleyecek.
            var videoDevice = settings?.CaptureSource == CaptureSource.Camera ? settings?.SelectedVideoDevice : null;
            var audioDevice = settings?.SelectedAudioDevice;

            var profile = settings?.GetSelectedProfile() ?? Profile.Default();

            // Ayarlardaki değerleri Profile'e aktar (UI'da seçilenler geçerli olsun)
            if (settings != null)
            {
                profile.Fps = settings.Fps > 0 ? settings.Fps : 30;
                profile.Width = settings.Width > 0 ? settings.Width : 1280;
                profile.Height = settings.Height > 0 ? settings.Height : 720;
                profile.VideoBitrateKbps = settings.VideoKbps > 0 ? settings.VideoKbps : 2500;
                profile.AudioBitrateKbps = settings.AudioKbps > 0 ? settings.AudioKbps : 128;
                profile.AudioDelayMs = settings.AudioDelayMs;
            }

            return await StartInternalAsync(profile, videoDevice, audioDevice, screenCapture, ct);
        }

        // Legacy interface methods
        async Task IStreamController.StartAsync(Profile profile, IEnumerable<StreamTarget> targets, CancellationToken ct)
        {
            _targets.Clear(); if (targets != null) _targets.AddRange(targets);
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
                _ffmpegProcess?.Dispose();
                _ffmpegProcess = null;
                IsRunning = false;
                try { _procCts?.Cancel(); } catch { }
                _procCts?.Dispose();
                _procCts = null;
            }
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        // ----------------- Internal Logic -----------------

        private async Task<StreamStartResult> StartInternalAsync(Profile profile, string? videoDeviceId, string? audioDeviceId, bool screenCapture, CancellationToken ct)
        {
            if (IsRunning) return StreamStartResult.Fail(StreamErrorCode.InvalidConfig, "Yayın zaten çalışıyor.");

            CurrentProfile = profile ?? Profile.Default();

            var enabledTargets = _targets.Where(t => t?.Enabled == true && !string.IsNullOrWhiteSpace(t.Url)).ToList();
            if (enabledTargets.Count == 0)
                return StreamStartResult.Fail(StreamErrorCode.InvalidConfig, "Etkin yayın hedefi yok. Lütfen en az bir hedef (RTMP) ekleyin.");

            // 1. Cihaz İsimlerini Çözümle (ID -> Friendly Name)
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

            // 2. Ses Gecikmesini ve Kayıt Yolunu Ayarlardan Oku
            var globalSettings = SettingsStore.Load();
            int delayMs = CurrentProfile.AudioDelayMs > 0 ? CurrentProfile.AudioDelayMs : globalSettings.AudioDelayMs;

            // Kayıt Klasörü Oluşturma
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

            // Overlay Pipe Adı
            var overlayPipeName = globalSettings.ShowOverlay ? "unicast_overlay" : null;

            // 3. Argümanları Oluştur
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

            _procCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ffmpegProcess = new FfmpegProcess();

            // Event Wiring
            _ffmpegProcess.OnLog += (line) =>
            {
                LastMessage = line;
                OnLog?.Invoke(this, line);
                if (line.Contains("Connection") || line.Contains("Reconnect")) LastAdvisory = line;
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
                IsRunning = false;
                OnExit?.Invoke(this, code ?? -1);
                OnLog?.Invoke(this, $"[ffmpeg] Çıkış kodu: {code}");
            };

            try
            {
                await _ffmpegProcess.StartAsync(args, _procCts.Token);
                IsRunning = true;
                OnLog?.Invoke(this, $"[start] Yayın başladı. Profil: {CurrentProfile.Name}");
                return StreamStartResult.Ok();
            }
            catch (Exception ex)
            {
                IsRunning = false;
                _ffmpegProcess = null;

                var msg = ex.Message.ToLowerInvariant();
                if (msg.Contains("camera") || msg.Contains("video device") || msg.Contains("busy"))
                    return StreamStartResult.Fail(StreamErrorCode.CameraBusy, "Kamera başlatılamadı. Başka bir uygulama (Zoom, Teams vb.) kamerayı kullanıyor olabilir.", ex.Message);

                if (msg.Contains("found") || msg.Contains("find"))
                    return StreamStartResult.Fail(StreamErrorCode.FfmpegNotFound, "FFmpeg yayın aracı bulunamadı.", ex.Message);

                return StreamStartResult.Fail(StreamErrorCode.Unknown, $"Yayın başlatılamadı: {ex.Message}", ex.Message);
            }
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
                        if (double.TryParse(v.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var f)) fps = f;
                    }
                    else if (p.StartsWith("bitrate=", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = p.Substring(8).ToLowerInvariant();
                        if (v.EndsWith("kbits/s") && double.TryParse(v.Replace("kbits/s", "").Trim().Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var kb)) br = kb;
                    }
                }
            }
            catch { }
            return new StreamMetric { TimestampUtc = tsUtc, Fps = fps, BitrateKbps = br };
        }
    }
}