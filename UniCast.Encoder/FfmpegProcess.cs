using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UniCast.Encoder
{
    public sealed class FfmpegProcess : IDisposable
    {
        public event Action<string>? OnLog;
        public event Action<string>? OnMetric;
        public event Action<int?>? OnExit;

        private Process? _proc;
        private string _lastErrorLine = "";

        // --- 1. GÜNCEL PATH ÇÖZÜMLEME (External Klasörü Destekli) ---
        public static string ResolveFfmpegPath()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 1. Öncelik: 'External' klasörü
            var bundledPath = Path.Combine(baseDir, "External", "ffmpeg.exe");
            if (File.Exists(bundledPath)) return bundledPath;

            // 2. Öncelik: Ana Dizin
            var localPath = Path.Combine(baseDir, "ffmpeg.exe");
            if (File.Exists(localPath)) return localPath;

            // 3. Öncelik: PATH Ortam Değişkenleri (YENİ EKLENDİ)
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var path in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    var fullPath = Path.Combine(path.Trim(), "ffmpeg.exe");
                    if (File.Exists(fullPath)) return fullPath;
                }
                catch { /* Erişim hatası vs. yut */ }
            }

            // 4. Hiçbiri yoksa sistem komutu olarak dene
            return "ffmpeg";
        }

        public Task StartAsync(string args, CancellationToken ct)
        {
            if (_proc is not null)
                throw new InvalidOperationException("FFmpeg already running.");

            var ffmpegPath = ResolveFfmpegPath();

            // Dosya kontrolü
            if (ffmpegPath != "ffmpeg" && !File.Exists(ffmpegPath))
                throw new FileNotFoundException("FFmpeg dosyası bulunamadı!", ffmpegPath);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true, // Loglar buradan akar
                RedirectStandardOutput = true // Bazen buraya da yazar
            };

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            // --- 2. ÇIKIŞ OLAYI VE HATA TERCÜMESİ (ESKİ KODDAN GERİ GELDİ) ---
            _proc.Exited += (_, __) =>
            {
                // İnsan okunabilir hata mesajı üret
                if (!string.IsNullOrWhiteSpace(_lastErrorLine))
                {
                    // ErrorDictionary sınıfın projede mevcut olduğu için bunu kullanıyoruz
                    var meaning = ErrorDictionary.Translate(_lastErrorLine);
                    OnLog?.Invoke("FFmpeg Hata Analizi: " + meaning);
                }

                OnExit?.Invoke(_proc?.ExitCode);
            };

            if (!_proc.Start())
                throw new InvalidOperationException("FFmpeg failed to start.");

            // --- 3. OKUMA DÖNGÜSÜ (STREAM READING - DAHA PERFORMANSLI) ---
            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = _proc.StandardError;
                    var buffer = new char[2048];

                    while (!_proc.HasExited && !ct.IsCancellationRequested)
                    {
                        var n = await reader.ReadAsync(buffer, 0, buffer.Length);
                        if (n > 0)
                        {
                            var chunk = new string(buffer, 0, n);

                            foreach (var line in chunk.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                var ln = line.Trim();
                                if (string.IsNullOrWhiteSpace(ln)) continue;

                                // Son hatayı sakla (Çıkışta analiz etmek için)
                                _lastErrorLine = ln;

                                if (IsMetric(ln))
                                    OnMetric?.Invoke(ln);
                                else
                                    OnLog?.Invoke(ln);
                            }
                        }
                        else
                        {
                            await Task.Delay(50, ct);
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke("FFmpeg Okuma Hatası: " + ex.Message);
                }
            }, ct);

            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (_proc is null) return;

            try
            {
                if (!_proc.HasExited)
                {
                    try
                    {
                        // Önce nazikçe 'q' gönderip kapatmayı deneyebiliriz ama 
                        // şimdilik Kill en garantisi.
                        _proc.Kill(true);
                    }
                    catch { }
                }
            }
            finally
            {
                await Task.Delay(120); // Kaynakların salınması için kısa bekleme
                _proc?.Dispose();
                _proc = null;
            }
        }

        // --- METRİK AYIKLAMA (REGEX) ---
        private static readonly Regex MetricRegex =
            new(@"frame=\s*\d+.*?fps=\s*([\d\.]+).*?bitrate=\s*([0-9\.kmbits\/]+).*?speed=\s*([0-9\.x]+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static bool IsMetric(string line)
            => !string.IsNullOrWhiteSpace(line) &&
               (MetricRegex.IsMatch(line) ||
                line.Contains("bitrate=") ||
                line.Contains("fps="));

        public void Dispose() => _ = StopAsync();
    }
}