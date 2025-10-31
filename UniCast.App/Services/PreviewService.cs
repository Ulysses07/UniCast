using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace UniCast.App.Services
{
    /// <summary>
    /// Düşük gecikmeli, sağlam kamera önizleme servisi (WPF uyumlu).
    /// x64 derleyin. Başka uygulama kamerayı tutuyorsa yeniden deneme yapar.
    /// </summary>
    public sealed class PreviewService : IDisposable
    {
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private VideoCapture? _cap;

        public event Action<BitmapSource?>? OnFrame;

        public bool IsRunning => _loop is not null;

        public Task StartAsync(int preferredIndex = -1, int width = 1280, int height = 720, int fps = 30)
        {
            if (_loop is not null) return Task.CompletedTask;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            _loop = Task.Run(() =>
            {
                try
                {
                    int index = ResolveCameraIndex(preferredIndex);
                    if (index < 0) throw new InvalidOperationException("Kamera bulunamadı.");

                    _cap = new VideoCapture(index, VideoCaptureAPIs.MSMF);
                    if (!_cap.IsOpened()) throw new InvalidOperationException("Kamera açılamadı.");

                    if (width > 0) _cap.Set(VideoCaptureProperties.FrameWidth, width);
                    if (height > 0) _cap.Set(VideoCaptureProperties.FrameHeight, height);
                    if (fps > 0) _cap.Set(VideoCaptureProperties.Fps, fps);

                    using var mat = new Mat();
                    while (!ct.IsCancellationRequested)
                    {
                        if (!_cap.Read(mat) || mat.Empty())
                        {
                            Task.Delay(30, ct).Wait(ct);
                            continue;
                        }

                        // BGR Mat -> WPF BitmapSource
                        var bmp = mat.ToWriteableBitmap();
                        bmp.Freeze(); // UI thread'e marshalling kolay
                        OnFrame?.Invoke(bmp);

                        Task.Delay(1000 / Math.Max(15, fps), ct).Wait(ct);
                    }
                }
                catch (OperationCanceledException) { /* normal durdurma */ }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Preview error: " + ex.Message);
                    OnFrame?.Invoke(null);
                }
                finally
                {
                    try { _cap?.Release(); } catch { }
                    _cap?.Dispose();
                    _cap = null;
                }
            }, ct);

            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (_loop is null) return;
            try
            {
                _cts?.Cancel();
                if (_loop is not null) await _loop;
            }
            finally
            {
                _loop = null;
                _cts?.Dispose();
                _cts = null;
            }
        }

        public void Dispose() => _ = StopAsync();

        private static int ResolveCameraIndex(int preferredIndex)
        {
            if (preferredIndex >= 0)
            {
                using var test = new VideoCapture(preferredIndex, VideoCaptureAPIs.MSMF);
                if (test.IsOpened()) return preferredIndex;
            }
            for (int i = 0; i < 10; i++)
            {
                using var cap = new VideoCapture(i, VideoCaptureAPIs.MSMF);
                if (cap.IsOpened()) return i;
            }
            return -1;
        }
    }
}
