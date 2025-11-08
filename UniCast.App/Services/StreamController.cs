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
    /// <summary>
    /// ffmpeg süreç yönetimi. Yeni akış modeli:
    /// - Etkin hedefler: TargetsView üzerinden eklenir (StreamTarget listesi)
    /// - Giriş kaynakları: SettingsData.DefaultCamera / DefaultMicrophone (yoksa testsrc/anullsrc)
    /// </summary>
    public sealed class StreamController : IStreamController, IAsyncDisposable
    {
        // ---- IStreamController üyeleri ----
        public bool IsRunning { get; private set; }
        public bool IsReconnecting { get; private set; } = false;

        public string? LastAdvisory { get; private set; }
        public string? LastMessage { get; private set; }
        public string? LastMetric { get; private set; }

        public Profile CurrentProfile { get; private set; } = Profile.Default();
        public IReadOnlyList<StreamTarget> Targets => _targets;

        public event EventHandler<string>? OnLog;
        public event EventHandler<StreamMetric>? OnMetric;
        public event EventHandler<int>? OnExit;

        private readonly List<StreamTarget> _targets = new();
        private readonly object _gate = new();
        private Process? _ffmpeg;
        private CancellationTokenSource? _procCts;

        // ---- hedef yönetimi ----
        public void AddTarget(StreamTarget target)
        {
            if (target == null) return;
            _targets.Add(target);
            OnLog?.Invoke(this, $"[targets] eklendi: {target.Platform} → {target.Url}");
        }

        public void RemoveTarget(StreamTarget target)
        {
            if (target == null) return;
            _targets.Remove(target);
            OnLog?.Invoke(this, $"[targets] çıkarıldı: {target.Platform} → {target.Url}");
        }

        // ---- Yeni başlangıç (profil+ayarlarla) ----
        public Task StartAsync(Profile profile, CancellationToken ct = default)
        {
            // SettingsData olmadan da çağrılabiliyordu; burada sadece profili güncelliyoruz.
            lock (_gate) { CurrentProfile = profile ?? Profile.Default(); }
            // SettingsData’ya ihtiyaç duyulan parametreler UI’dan geldiği için
            // StartAsync(IEnumerable<TargetItem>, SettingsData, ...) yolu tercih edilmeli.
            return StartInternalAsync(null, null, useScreen: false, ct);
        }

        // Eski imza – UI ControlViewModel’den geliyor
        public async Task StartAsync(IEnumerable<TargetItem> items, SettingsData settings, CancellationToken ct = default)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            _targets.Clear();
            foreach (var i in items)
            {
                if (i == null) continue;
                if (!i.Enabled) continue;

                _targets.Add(new StreamTarget
                {
                    Platform = i.Platform,
                    Url = i.Url,
                    Enabled = i.Enabled
                });
            }

            lock (_gate)
            {
                CurrentProfile = settings.GetSelectedProfile();
            }

            await StartInternalAsync(settings.DefaultCamera,
                                     settings.DefaultMicrophone,
                                     useScreen: false,
                                     ct);
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

        // ======================================================
        // ===============  PRIVATE İÇ AKIŞ  ====================
        // ======================================================
        private async Task StartInternalAsync(
            string? videoDevice,
            string? audioDevice,
            bool useScreen,
            CancellationToken ct)
        {
            lock (_gate)
            {
                if (IsRunning) throw new InvalidOperationException("Yayın zaten çalışıyor.");
            }

            var enabledTargets = _targets.Where(t => t?.Enabled == true && !string.IsNullOrWhiteSpace(t.Url)).ToList();
            if (enabledTargets.Count == 0)
                throw new InvalidOperationException("Etkin yayın hedefi yok. En az bir hedef ekleyin.");

            var ffmpegPath = ResolveFfmpegPath();
            if (ffmpegPath == null)
                throw new FileNotFoundException("ffmpeg bulunamadı. Uygulama dizinine veya PATH'e ekleyin.");

            // ViewModel’lerin doldurduğu ayarları SettingsService üzerinden alıyorsanız
            // oradan SettingsData geçebilirsiniz. Burada basit bir SettingsData türetiyorum:
            var s = new SettingsData
            {
                DefaultCamera = videoDevice,
                DefaultMicrophone = audioDevice,
                // Control/Settings VM zaten bunları dolduruyor; fallback olsun diye:
                Width = CurrentProfile.Width,
                Height = CurrentProfile.Height,
                Fps = CurrentProfile.Fps,
                Encoder = CurrentProfile.Encoder ?? "libx264",
                VideoKbps = CurrentProfile.VideoKbps,
                AudioKbps = CurrentProfile.AudioKbps,
                EnableLocalRecord = CurrentProfile.EnableLocalRecord,
                RecordFolder = CurrentProfile.RecordFolder
            };

            // Argümanları kur
            var build = FfmpegArgsBuilder.BuildSingleEncodeMultiRtmpWithOverlay(
                s,
                enabledTargets,
                overlayX: 0,
                overlayY: 0
            );

            if (!string.IsNullOrWhiteSpace(build.Advisory))
                LastAdvisory = build.Advisory;

            _procCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = build.Args,
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
                var line = e.Data;

                LastMessage = line;
                OnLog?.Invoke(this, line);

                // kaba metrik ayrıştırma
                if (line.Contains("bitrate=") || line.Contains("fps="))
                {
                    var metric = TryParseMetric(line, DateTime.UtcNow);
                    if (metric != null)
                    {
                        LastMetric = $"fps={metric.Fps?.ToString("0.#") ?? "?"} bitrate={metric.BitrateKbps?.ToString("0.#") ?? "?"}kbits/s";
                        OnMetric?.Invoke(this, metric);
                    }
                }
                else if (line.Contains("Connection") || line.Contains("Reconnect"))
                {
                    LastAdvisory = line;
                }
            };

            p.Exited += (sdr, e) =>
            {
                lock (_gate) { IsRunning = false; }
                var code = p.ExitCode;
                OnExit?.Invoke(this, code);
                OnLog?.Invoke(this, $"[ffmpeg] exited with code {code}");
                try { _procCts?.Cancel(); } catch { }
            };

            if (!p.Start())
                throw new InvalidOperationException("ffmpeg başlatılamadı.");

            p.BeginErrorReadLine();

            lock (_gate)
            {
                _ffmpeg = p;
                IsRunning = true;
            }

            OnLog?.Invoke(this, $"[start] profil: {CurrentProfile.Name}, hedef: {enabledTargets.Count}");
            await Task.CompletedTask;
        }

        private static string? ResolveFfmpegPath()
        {
            // 1) Uygulama klasörü
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var local = Path.Combine(baseDir, "ffmpeg.exe");
                if (File.Exists(local)) return local;
            }
            catch { }

            // 2) PATH
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

            // 3) WSL/Linux (geliştirici makinesi ihtimali)
            try
            {
                var which = "/usr/bin/ffmpeg";
                if (File.Exists(which)) return which;
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

            if (fps == null && br == null) return null;
            return new StreamMetric { TimestampUtc = tsUtc, Fps = fps, BitrateKbps = br };
        }
    }
}
