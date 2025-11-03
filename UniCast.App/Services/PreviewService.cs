using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DirectShowLib;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using UniCast.Core.Settings;

namespace UniCast.App.Services
{
    public sealed class PreviewService : IDisposable
    {
        public event Action<ImageSource>? OnFrame;

        private VideoCapture? _cap;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private readonly object _gate = new();

        public bool IsRunning { get; private set; }

        public Task StartAsync(int width, int height, int fps)
        {
            lock (_gate)
            {
                if (IsRunning) return Task.CompletedTask;
                IsRunning = true;
                _cts = new CancellationTokenSource();
            }

            try
            {
                var s = SettingsStore.Load();
                var want = (s.DefaultCamera ?? "").Trim(); // ör: "DroidCam", "DroidCam Video"

                // DirectShow cihazları
                var dsDevs = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
                if (dsDevs is null || dsDevs.Length == 0)
                    throw new InvalidOperationException("Kamera bulunamadı (DirectShow).");

                string[] allNames = dsDevs.Select(d => d.Name).ToArray();

                var candidates = allNames
                    .Where(n => !string.IsNullOrWhiteSpace(want) && string.Equals(n, want, StringComparison.Ordinal))
                    .Concat(allNames.Where(n => !string.IsNullOrWhiteSpace(want) && n.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0))
                    .Concat(allNames) // hepsini dene
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                var prioritized = candidates
                    .OrderByDescending(n => n.IndexOf("droidcam", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToArray();

                bool opened = false;
                for (int i = 0; i < prioritized.Length && !opened; i++)
                {
                    var name = prioritized[i];

                    // DSHOW
                    if (TryOpen($"video={name}", VideoCaptureAPIs.DSHOW, width, height, fps)) { opened = true; break; }
                    // FFMPEG backend
                    if (TryOpen($"video={name}", VideoCaptureAPIs.FFMPEG, width, height, fps)) { opened = true; break; }
                    // MSMF index
                    int msIndex = Array.IndexOf(allNames, name);
                    if (msIndex >= 0 && TryOpen(msIndex, VideoCaptureAPIs.MSMF, width, height, fps)) { opened = true; break; }
                }

                if (!opened)
                {
                    if (!TryOpen(0, VideoCaptureAPIs.DSHOW, width, height, fps))
                        throw new InvalidOperationException($"Kamera açılamadı: \"{(string.IsNullOrWhiteSpace(want) ? "(seçilmedi)" : want)}\"");
                }

                var ct = _cts!.Token;
                _loop = Task.Run(() =>
                {
                    using var mat = new Mat();
                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            if (!_cap!.Read(mat) || mat.Empty())
                            {
                                Task.Delay(10, ct).Wait(ct);
                                continue;
                            }
                            var bmp = BitmapSourceConverter.ToBitmapSource(mat);
                            bmp.Freeze();
                            OnFrame?.Invoke(bmp);
                        }
                        catch (OperationCanceledException) { break; }
                        catch { Task.Delay(20, ct).Wait(ct); }
                    }
                }, ct);
            }
            catch
            {
                StopAsync().GetAwaiter().GetResult();
                throw;
            }

            return Task.CompletedTask;
        }

        private bool TryOpen(string deviceString, VideoCaptureAPIs api, int w, int h, int fps)
        {
            try
            {
                _cap?.Release(); _cap?.Dispose();
                _cap = new VideoCapture(deviceString, api);
                if (!_cap.IsOpened()) return false;
                ApplyFormat(w, h, fps);
                return _cap.IsOpened();
            }
            catch { return false; }
        }

        private bool TryOpen(int index, VideoCaptureAPIs api, int w, int h, int fps)
        {
            try
            {
                _cap?.Release(); _cap?.Dispose();
                _cap = new VideoCapture(index, api);
                if (!_cap.IsOpened()) return false;
                ApplyFormat(w, h, fps);
                return _cap.IsOpened();
            }
            catch { return false; }
        }

        private void ApplyFormat(int w, int h, int fps)
        {
            _cap!.Set(VideoCaptureProperties.FrameWidth, w);
            _cap.Set(VideoCaptureProperties.FrameHeight, h);
            _cap.Set(VideoCaptureProperties.Fps, fps);
        }

        public async Task StopAsync()
        {
            Task? loop;
            lock (_gate)
            {
                if (!IsRunning) return;
                IsRunning = false;
                _cts?.Cancel();
                loop = _loop;
                _loop = null;
            }
            if (loop != null) { try { await loop; } catch { } }
            _cap?.Release(); _cap?.Dispose(); _cap = null;
            _cts?.Dispose(); _cts = null;
        }

        public void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
        }
    }
}
