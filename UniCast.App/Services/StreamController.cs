using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Core.Models;
using UniCast.Core.Settings;
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

        public Profile CurrentProfile { get; private set; } = Profile.Default();
        private readonly List<StreamTarget> _targets = new();

        public event EventHandler<string>? OnLog;
        public event EventHandler<StreamMetric>? OnMetric;
        public event EventHandler<int>? OnExit;

        private readonly object _gate = new();
        private Process? _ffmpeg;
        private CancellationTokenSource? _procCts;

        // ----------------- Public API -----------------

        public void AddTarget(StreamTarget target)
        {
            if (target == null) return;
            _targets.Add(target);
            OnLog?.Invoke(this, $"[targets] eklendi: {target.DisplayName ?? target.Platform.ToString()}");
        }

        public void RemoveTarget(StreamTarget target)
        {
            if (target == null) return;
            _targets.Remove(target);
            OnLog?.Invoke(this, $"[targets] çıkarıldı: {target.DisplayName ?? target.Platform.ToString()}");
        }

        public Task StartAsync(Profile profile, CancellationToken ct = default)
            => StartInternalAsync(profile, videoDevice: null, audioDevice: null, screenCapture: false, ct);

        public Task StartAsync(Profile profile, string? videoDevice, string? audioDevice, bool screenCapture, CancellationToken ct = default)
            => StartInternalAsync(profile, videoDevice, audioDevice, screenCapture, ct);

        public async Task StopAsync(CancellationToken ct = default)
        {
            Process? p;
            lock (_gate) { p = _ffmpeg; }
            if (p == null)
            {
                OnLog?.Invoke(this, "[stop] aktif süreç yok.");
                return;
            }

            try
            {
                if (!p.HasExited)
                {
                    await TrySendQuitAsync(p, ct);
                    if (!p.WaitForExit(2000))
                    {
                        OnLog?.Invoke(this, "[stop] graceful quit başarısız, Kill() çağrılıyor.");
                        p.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(this, $"[stop][error] {ex.Message}");
            }
            finally
            {
                lock (_gate) { IsRunning = false; _ffmpeg = null; }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await StopAsync(); } catch { }
            _procCts?.Dispose();
        }

        // ---- IStreamController eski imzalar (derleme uyumu için) ----

        async Task IStreamController.StartAsync(Profile profile, IEnumerable<StreamTarget> targets, CancellationToken ct)
        {
            _targets.Clear();
            if (targets != null) _targets.AddRange(targets);
            await StartInternalAsync(profile, null, null, false, ct);
        }

        async Task IStreamController.StartAsync(IEnumerable<TargetItem> items, SettingsData settings, CancellationToken ct)
        {
            // UI’nin Targets paneli TargetItem tutuyor. Onları burada StreamTarget’a map’leyelim:
            var mapped = items?.ToStreamTargets() ?? Array.Empty<StreamTarget>();
            _targets.Clear();
            _targets.AddRange(mapped);

            // SettingsData tarafında cihaz adları varsa kullan; yoksa null geç.
            var screenCapture = settings?.CaptureSource == CaptureSource.Screen;
            var videoDevice = settings?.CaptureSource == CaptureSource.Camera ? settings?.SelectedVideoDevice : null;
            var audioDevice = settings?.SelectedAudioDevice;

            var profile = settings?.GetSelectedProfile() ?? Profile.Default();
            await StartInternalAsync(profile, videoDevice, audioDevice, screenCapture, ct);
        }

        // ----------------- Internal -----------------

        private async Task StartInternalAsync(Profile profile, string? videoDevice, string? audioDevice, bool screenCapture, CancellationToken ct)
        {
            lock (_gate)
            {
                if (IsRunning)
                    throw new InvalidOperationException("Yayın zaten çalışıyor.");
                CurrentProfile = profile ?? Profile.Default();
            }

            var enabledTargets = _targets.Where(t => t?.Enabled == true && !string.IsNullOrWhiteSpace(t.Url)).ToList();
            if (enabledTargets.Count == 0)
                throw new InvalidOperationException("Etkin yayın hedefi yok. En az bir hedef ekleyin.");

            var ffmpegPath = ResolveFfmpegPath();
            if (ffmpegPath == null)
                throw new FileNotFoundException("ffmpeg bulunamadı. Uygulama dizinine veya PATH'e ekleyin.");

            var args = FfmpegArgsBuilder.BuildFfmpegArgs(
                CurrentProfile,
                enabledTargets,
                videoDevice,
                audioDevice,
                screenCapture
            );

            _procCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    StandardErrorEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };

            p.ErrorDataReceived += (s, e) =>
            {
                if (e.Data is null) return;
                var line = e.Data;

                LastMessage = line;
                OnLog?.Invoke(this, line);

                if (line.Contains("bitrate=") || line.Contains("fps="))
                {
                    var metric = TryParseMetric(line, DateTime.UtcNow);
                    if (metric != null)
                    {
                        LastMetric = $"fps={metric.Fps?.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) ?? "?"} " +
                                     $"bitrate={(metric.BitrateKbps?.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) ?? "?")}kbits/s";
                        OnMetric?.Invoke(this, metric);
                    }
                }
                else if (line.Contains("Connection") || line.Contains("Reconnect"))
                {
                    LastAdvisory = line;
                }
            };

            p.Exited += (s, e) =>
            {
                lock (_gate) { IsRunning = false; }
                var code = p.ExitCode;
                OnExit?.Invoke(this, code);
                OnLog?.Invoke(this, $"[ffmpeg] exited with code {code}");
                _procCts?.Cancel();
            };

            if (!p.Start())
                throw new InvalidOperationException("ffmpeg başlatılamadı.");

            p.BeginErrorReadLine();

            lock (_gate)
            {
                _ffmpeg = p;
                IsRunning = true;
            }

            OnLog?.Invoke(this, $"[start] profil: {CurrentProfile.Name}, hedef: {enabledTargets.Count}, screenCapture={(screenCapture ? "on" : "off")}, video={videoDevice ?? "-"}, audio={audioDevice ?? "-"}");
            await Task.CompletedTask;
        }

        private static string? ResolveFfmpegPath()
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var local = Path.Combine(baseDir, "ffmpeg.exe");
                if (File.Exists(local)) return local;
            }
            catch { }

            try
            {
                var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
                foreach (var dir in paths)
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }
                }
            }
            catch { }

            return null;
        }

        private static async Task TrySendQuitAsync(Process p, CancellationToken ct)
        {
            try
            {
                if (p.StartInfo.RedirectStandardInput && p.StandardInput.BaseStream.CanWrite)
                {
                    await p.StandardInput.WriteAsync("q");
                    await p.StandardInput.FlushAsync();
                }
            }
            catch { }
            try { await Task.Delay(300, ct); } catch { }
        }

        private static StreamMetric TryParseMetric(string line, DateTime tsUtc)
        {
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
                        if (double.TryParse(v.Replace(",", "."),
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var f)) fps = f;
                    }
                    else if (p.StartsWith("bitrate=", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = p.Substring(8).ToLowerInvariant();
                        if (v.EndsWith("kbits/s") && double.TryParse(
                                v.Replace("kbits/s", "").Trim().Replace(",", "."),
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var kb))
                            br = kb;
                    }
                }
            }
            catch { }

            return new StreamMetric { TimestampUtc = tsUtc, Fps = fps, BitrateKbps = br };
        }
    }
}
