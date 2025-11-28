using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace UniCast.App.Services
{
    public sealed class PreviewService : IDisposable
    {
        public event Action<ImageSource>? OnFrame;
        public bool IsRunning { get; private set; }

        private VideoCapture? _capture;
        private Task? _previewTask;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        // DÜZELTME: Mat'i bir kez oluştur, her karede yeniden kullanma
        private Mat? _frame;

        public async Task StartAsync(int cameraIndex, int width, int height, int fps)
        {
            if (IsRunning || _disposed) return;

            if (cameraIndex < 0) cameraIndex = 0;

            try
            {
                _capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);

                if (!_capture.IsOpened())
                {
                    System.Diagnostics.Debug.WriteLine("Preview: Kamera açılamadı.");
                    return;
                }

                _capture.Set(VideoCaptureProperties.FrameWidth, width);
                _capture.Set(VideoCaptureProperties.FrameHeight, height);
                _capture.Set(VideoCaptureProperties.Fps, fps);

                // DÜZELTME: Eski CTS'i dispose et
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                IsRunning = true;

                // DÜZELTME: Mat'i önceden oluştur
                _frame = new Mat();

                _previewTask = Task.Run(() => CaptureLoop(_cts.Token), _cts.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Preview Error: {ex.Message}");
                await StopAsync();
            }
        }

        private void CaptureLoop(CancellationToken ct)
        {
            // DÜZELTME: Mat artık field olarak tutulduğu için using kullanmıyoruz
            // Döngü dışında dispose edilecek

            while (!ct.IsCancellationRequested && _capture != null && _capture.IsOpened() && _frame != null)
            {
                try
                {
                    if (!_capture.Read(_frame) || _frame.Empty())
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    // DÜZELTME: WriteableBitmap oluştur ve hemen freeze et
                    // ToWriteableBitmap() her seferinde yeni nesne oluşturur
                    // Bu kaçınılmaz, ama frame'i reuse ediyoruz
                    WriteableBitmap? bmp = null;
                    try
                    {
                        bmp = _frame.ToWriteableBitmap();
                        bmp.Freeze(); // Thread-safe yapar
                        OnFrame?.Invoke(bmp);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Preview Frame Error: {ex.Message}");
                    }
                    // NOT: WriteableBitmap IDisposable değil, GC tarafından temizlenir
                    // Freeze() çağrıldıktan sonra immutable olduğu için güvenlidir

                    // FPS kontrolü
                    Thread.Sleep(33); // ~30 FPS
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Preview Loop Error: {ex.Message}");
                    // Döngüden çıkma, bir sonraki kareyi dene
                }
            }
        }

        public async Task StopAsync()
        {
            if (!IsRunning && _capture == null) return;

            IsRunning = false;

            // CTS'i iptal et
            if (_cts != null)
            {
                try
                {
                    _cts.Cancel();
                }
                catch { }
            }

            // Task'ın bitmesini bekle
            if (_previewTask != null)
            {
                try
                {
                    await _previewTask.ConfigureAwait(false);
                }
                catch { }
                _previewTask = null;
            }

            // DÜZELTME: Mat'i dispose et
            if (_frame != null)
            {
                try
                {
                    _frame.Dispose();
                }
                catch { }
                _frame = null;
            }

            // Capture'ı temizle
            if (_capture != null)
            {
                try
                {
                    _capture.Release();
                    _capture.Dispose();
                }
                catch { }
                _capture = null;
            }

            // DÜZELTME: CTS'i dispose et
            if (_cts != null)
            {
                try
                {
                    _cts.Dispose();
                }
                catch { }
                _cts = null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Event'i temizle
            OnFrame = null;

            StopAsync().GetAwaiter().GetResult();
        }
    }
}