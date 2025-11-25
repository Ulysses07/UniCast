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
        public bool IsRunning { get; private set; }
        public bool IsReconnecting { get; private set; } = false;
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

        public void AddTarget(StreamTarget target) { if (target != null) { _targets.Add(target); OnLog?.Invoke(this, $"[targets] Eklendi: {target.DisplayName}"); } }
        public void RemoveTarget(StreamTarget target) { if (target != null) { _targets.Remove(target); OnLog?.Invoke(this, $"[targets] Çıkarıldı: {target.DisplayName}"); } }

        public async Task StartAsync(Profile profile, CancellationToken ct = default)
        {
            var result = await StartWithResultAsync(profile, ct);
            if (!result.Success) throw new InvalidOperationException(result.UserMessage ?? "Yayın başlatılamadı.");
        }

        public Task<StreamStartResult> StartWithResultAsync(Profile profile, CancellationToken ct = default) => StartInternalAsync(profile, null, null, false, ct);
        public Task<StreamStartResult> StartWithResultAsync(Profile profile, string? videoDevice, string? audioDevice, bool screenCapture, CancellationToken ct = default) => StartInternalAsync(profile, videoDevice, audioDevice, screenCapture, ct);

        public async Task<StreamStartResult> StartWithResultAsync(IEnumerable<TargetItem> items, SettingsData settings, CancellationToken ct)
        {
            var mapped = items?.ToStreamTargets() ?? Array.Empty<StreamTarget>();
            _targets.Clear();
            _targets.AddRange(mapped);

            var screenCapture = settings?.CaptureSource == CaptureSource.Screen;
            var videoDevice = settings?.CaptureSource == CaptureSource.Camera ? settings?.SelectedVideoDevice : null;
            var audioDevice = settings?.SelectedAudioDevice;
            var profile = settings?.GetSelectedProfile() ?? Profile.Default();

            return await StartInternalAsync(profile, videoDevice, audioDevice, screenCapture, ct);
        }

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
            if (_ffmpegProcess == null) { OnLog?.Invoke(this, "[stop] Süreç yok."); return; }
            try { await _ffmpegProcess.StopAsync(); }
            catch (Exception ex) { OnLog?.Invoke(this, $"[stop] Hata: {ex.Message}"); }
            finally { _ffmpegProcess?.Dispose(); _ffmpegProcess = null; IsRunning = false; _procCts?.Cancel(); _procCts = null; }
        }
        public async ValueTask DisposeAsync() => await StopAsync();

        private async Task<StreamStartResult> StartInternalAsync(Profile profile, string? videoDeviceId, string? audioDeviceId, bool screenCapture, CancellationToken ct)
        {
            if (IsRunning) return StreamStartResult.Fail(StreamErrorCode.InvalidConfig, "Yayın zaten çalışıyor.");
            CurrentProfile = profile ?? Profile.Default();

            var enabledTargets = _targets.Where(t => t?.Enabled == true && !string.IsNullOrWhiteSpace(t.Url)).ToList();
            if (enabledTargets.Count == 0) return StreamStartResult.Fail(StreamErrorCode.InvalidConfig, "Yayın hedefi seçilmedi.");

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

            var args = FfmpegArgsBuilder.BuildFfmpegArgs(
                CurrentProfile,
                enabledTargets,
                finalVideoName,
                finalAudioName,
                screenCapture,
                delayMs,
                recordPath
            );

            _procCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ffmpegProcess = new FfmpegProcess();

            _ffmpegProcess.OnLog += (line) => { LastMessage = line; OnLog?.Invoke(this, line); if (line.Contains("Connection")) LastAdvisory = line; };
            _ffmpegProcess.OnMetric += (line) => {
                var m = TryParseMetric(line, DateTime.UtcNow);
                if (m != null) { LastMetric = $"fps={m.Fps:0.#}"; OnMetric?.Invoke(this, m); }
            };
            _ffmpegProcess.OnExit += (code) => { IsRunning = false; OnExit?.Invoke(this, code ?? -1); };

            try
            {
                await _ffmpegProcess.StartAsync(args, _procCts.Token);
                IsRunning = true;
                OnLog?.Invoke(this, $"[start] Başladı: {CurrentProfile.Name}");
                return StreamStartResult.Ok();
            }
            catch (Exception ex)
            {
                IsRunning = false; _ffmpegProcess = null;
                var msg = ex.Message.ToLowerInvariant();
                if (msg.Contains("camera") || msg.Contains("video device")) return StreamStartResult.Fail(StreamErrorCode.CameraBusy, "Kamera meşgul.", ex.Message);
                if (msg.Contains("found")) return StreamStartResult.Fail(StreamErrorCode.FfmpegNotFound, "FFmpeg bulunamadı.", ex.Message);
                return StreamStartResult.Fail(StreamErrorCode.Unknown, $"Hata: {ex.Message}", ex.Message);
            }
        }

        private static StreamMetric TryParseMetric(string line, DateTime tsUtc)
        {
            if (string.IsNullOrWhiteSpace(line)) return new StreamMetric { TimestampUtc = tsUtc };
            double? fps = null; double? br = null;
            try
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    if (p.StartsWith("fps=", StringComparison.OrdinalIgnoreCase) && double.TryParse(p.Substring(4).Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var f)) fps = f;
                    if (p.StartsWith("bitrate=", StringComparison.OrdinalIgnoreCase) && double.TryParse(p.Substring(8).Replace("kbits/s", "").Trim().Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var b)) br = b;
                }
            }
            catch { }
            return new StreamMetric { TimestampUtc = tsUtc, Fps = fps, BitrateKbps = br };
        }
    }
}