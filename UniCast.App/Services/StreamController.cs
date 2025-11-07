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
using UniCast.Encoder;              // FfmpegArgsBuilder
using UniCast.Encoder.Extensions;   // ResolveUrl()

namespace UniCast.App.Services
{
    /// <summary>
    /// FFmpeg tabanlı yayın denetleyicisi – bir profili ve birden çok hedefi yönetir.
    /// </summary>
    public sealed class StreamController : IStreamController, IAsyncDisposable
    {
        // --- Public API (IStreamController) ---
        public bool IsRunning { get; private set; }
        public bool IsReconnecting => _isReconnecting;
        public Profile CurrentProfile { get; private set; } = Profile.Default();
        public IReadOnlyList<StreamTarget> Targets => _targets;

        public string? LastAdvisory { get; private set; }
        public string? LastMessage { get; private set; }
        public string? LastMetric { get; private set; }

        public event EventHandler<string>? OnLog;
        public event EventHandler<StreamMetric>? OnMetric;
        public event EventHandler<int /*exitCode*/>? OnExit;

        // --- Private fields ---
        private readonly List<StreamTarget> _targets = new();
        private Process? _ffmpeg;
        private readonly object _gate = new();
        private CancellationTokenSource? _procCts;
        private bool _isReconnecting;
        private SettingsData? _settings; // gerçek cihazlar ve encode ayarları için

        // --- Targets management ---
        public void AddTarget(StreamTarget target)
        {
            if (target == null) return;
            _targets.Add(target);
            OnLog?.Invoke(this, $"[targets] eklendi: {(target.DisplayName ?? target.Platform.ToString())}");
        }

        public void RemoveTarget(StreamTarget target)
        {
            if (target == null) return;
            _targets.Remove(target);
            OnLog?.Invoke(this, $"[targets] çıkarıldı: {(target.DisplayName ?? target.Platform.ToString())}");
        }

        // --- Start/Stop lifecycle ---
        public async Task StartAsync(Profile profile, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (IsRunning)
                    throw new InvalidOperationException("Yayın zaten çalışıyor.");

                CurrentProfile = profile ?? Profile.Default();
            }

            var enabledTargets = _targets.Where(t => t?.Enabled == true).ToList();
            if (enabledTargets.Count == 0)
                throw new InvalidOperationException("Etkin yayın hedefi yok. En az bir hedef ekleyin.");

            // Ayarları yükle (dışarıdan verilmediyse)
            var s = _settings ?? SettingsStore.Load();

            // FFmpeg yolunu çöz
            var ffmpegPath = ResolveFfmpegPath();
            if (ffmpegPath == null)
                throw new FileNotFoundException("ffmpeg bulunamadı. Uygulama dizinine veya PATH'e ekleyin.");

            // Args üret – gerçek cihazları dshow ile kullan
            var args = FfmpegArgsBuilder.BuildSingleEncodeMultiRtmp(s, enabledTargets, includeOverlayPipe: false);

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

            p.ErrorDataReceived += (sdr, e) =>
            {
                if (e.Data is null) return;

                LastMessage = e.Data;
                OnLog?.Invoke(this, e.Data);

                var line = e.Data;
                var metric = TryParseMetric(line, DateTime.UtcNow);
                if (metric is not null)
                {
                    OnMetric?.Invoke(this, metric);
                    LastMetric = BuildMetricText(metric);
                }

                if (line.Contains("testsrc", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("anullsrc", StringComparison.OrdinalIgnoreCase))
                {
                    LastAdvisory = "Gerçek cihaz bulunamadı; test kaynakları kullanılıyor.";
                }
            };

            p.Exited += (sdr, e) =>
            {
                lock (_gate) { IsRunning = false; }
                var code = p.ExitCode;
                OnExit?.Invoke(this, code);
                OnLog?.Invoke(this, $"[ffmpeg] exited with code {code}");
                _procCts?.Cancel();
                _isReconnecting = false;
            };

            if (!p.Start())
                throw new InvalidOperationException("ffmpeg başlatılamadı.");

            p.BeginErrorReadLine();

            lock (_gate)
            {
                _ffmpeg = p;
                IsRunning = true;
            }

            OnLog?.Invoke(this, $"[start] profil: {CurrentProfile.Name}, hedef sayısı: {enabledTargets.Count}");
            await Task.CompletedTask;
        }

        public async Task StartAsync(Profile profile, IEnumerable<StreamTarget> targets, CancellationToken ct)
        {
            _targets.Clear();
            if (targets != null) _targets.AddRange(targets);
            await StartAsync(profile, ct);
        }

        public async Task StartAsync(IEnumerable<TargetItem> targets, SettingsData settings, CancellationToken ct)
        {
            _targets.Clear();
            if (targets != null)
            {
                foreach (var i in targets)
                {
                    _targets.Add(new StreamTarget
                    {
                        Platform = i.Platform,
                        DisplayName = i.Name,
                        StreamKey = i.Key,
                        Url = i.Url,
                        Enabled = i.Enabled
                    });
                }
            }

            // Bu çağrıda SettingsData geldi: gerçek cihaz bilgileri burada
            _settings = settings;
            var p = CurrentProfile ?? Profile.Default();
            await StartAsync(p, ct);
        }

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
                _isReconnecting = false;
                if (!p.HasExited)
                {
                    await TrySendQuitAsync(p, ct);

                    var waitMs = 2000;
                    if (!p.WaitForExit(waitMs))
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
                lock (_gate)
                {
                    IsRunning = false;
                    _ffmpeg = null;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await StopAsync(); } catch { /* ignore */ }
            _procCts?.Dispose();
        }

        // --- Helpers ---

        private static string? ResolveFfmpegPath()
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var local = Path.Combine(baseDir, "ffmpeg.exe");
                if (File.Exists(local)) return local;
            }
            catch { /* ignore */ }

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
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }

            try
            {
                var which = "/usr/bin/ffmpeg";
                if (File.Exists(which)) return which;
            }
            catch { /* ignore */ }

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
            catch { /* ignore */ }

            try { await Task.Delay(300, ct); } catch { /* ignore */ }
        }

        private static StreamMetric? TryParseMetric(string line, DateTime tsUtc)
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
                                System.Globalization.CultureInfo.InvariantCulture, out var f))
                            fps = f;
                    }
                    else if (p.StartsWith("bitrate=", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = p.Substring(8).ToLowerInvariant();
                        if (v.EndsWith("kbits/s") && double.TryParse(
                                v.Replace("kbits/s", "").Trim().Replace(",", "."),
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var kb))
                            br = kb; // kbps
                    }
                }
            }
            catch { /* ignore */ }

            if (fps == null && br == null) return null;
            return new StreamMetric { TimestampUtc = tsUtc, Fps = fps, BitrateKbps = br };
        }

        private static string BuildMetricText(StreamMetric m)
        {
            var fps = m.Fps.HasValue ? $"{m.Fps:0.#} fps" : null;
            var br = m.BitrateKbps.HasValue ? $"{m.BitrateKbps:0.#} kbps" : null;
            if (fps != null && br != null) return $"{fps}, {br}";
            return fps ?? br ?? string.Empty;
        }
    }
}
