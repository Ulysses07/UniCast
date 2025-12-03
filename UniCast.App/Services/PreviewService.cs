using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace UniCast.App.Services
{
    /// <summary>
    /// Kamera önizleme servisi.
    /// OpenCV kullanarak kameradan frame alır ve WPF'e aktarır.
    /// DÜZELTME: Thread.Sleep yerine async-friendly Task.Delay kullanımı.
    /// </summary>
    public sealed class PreviewService : IDisposable
    {
        public event Action<ImageSource>? OnFrame;
        public bool IsRunning { get; private set; }

        private VideoCapture? _capture;
        private Task? _previewTask;
        private CancellationTokenSource? _cts;
        private bool _disposed;

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

                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                IsRunning = true;

                _frame = new Mat();

                _previewTask = CaptureLoopAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Preview Error: {ex.Message}");
                await StopAsync();
            }
        }

        /// <summary>
        /// DÜZELTME: Async capture loop - Thread.Sleep yerine Task.Delay
        /// </summary>
        private async Task CaptureLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _capture != null && _capture.IsOpened() && _frame != null)
            {
                try
                {
                    // Frame okuma (senkron - OpenCV kısıtlaması)
                    bool readSuccess = false;
                    try
                    {
                        readSuccess = _capture.Read(_frame);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Preview Read Error: {ex.Message}");
                    }

                    if (!readSuccess || _frame.Empty())
                    {
                        // DÜZELTME: Thread.Sleep yerine Task.Delay
                        await Task.Delay(10, ct);
                        continue;
                    }

                    WriteableBitmap? bmp = null;
                    try
                    {
                        bmp = _frame.ToWriteableBitmap();
                        bmp.Freeze();
                        OnFrame?.Invoke(bmp);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Preview Frame Error: {ex.Message}");
                    }

                    // DÜZELTME: Thread.Sleep yerine Task.Delay (async-friendly)
                    await Task.Delay(Constants.Preview.FrameIntervalMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Preview Loop Error: {ex.Message}");

                    // Hata durumunda kısa bekle
                    try
                    {
                        await Task.Delay(100, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        public async Task StopAsync()
        {
            if (!IsRunning && _capture == null) return;

            IsRunning = false;

            if (_cts != null)
            {
                try
                {
                    _cts.Cancel();
                }
                catch (Exception ex)
                {
                    // DÜZELTME v26: Boş catch'e loglama eklendi
                    System.Diagnostics.Debug.WriteLine($"[PreviewService.StopAsync] CTS cancel hatası: {ex.Message}");
                }
            }

            if (_previewTask != null)
            {
                try
                {
                    await _previewTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // DÜZELTME v26: Boş catch'e loglama eklendi
                    System.Diagnostics.Debug.WriteLine($"[PreviewService.StopAsync] Task bekleme hatası: {ex.Message}");
                }
                _previewTask = null;
            }

            if (_frame != null)
            {
                try
                {
                    _frame.Dispose();
                }
                catch (Exception ex)
                {
                    // DÜZELTME v26: Boş catch'e loglama eklendi
                    System.Diagnostics.Debug.WriteLine($"[PreviewService.StopAsync] Frame dispose hatası: {ex.Message}");
                }
                _frame = null;
            }

            if (_capture != null)
            {
                try
                {
                    _capture.Release();
                    _capture.Dispose();
                }
                catch (Exception ex)
                {
                    // DÜZELTME v26: Boş catch'e loglama eklendi
                    System.Diagnostics.Debug.WriteLine($"[PreviewService.StopAsync] Capture dispose hatası: {ex.Message}");
                }
                _capture = null;
            }

            if (_cts != null)
            {
                try
                {
                    _cts.Dispose();
                }
                catch (Exception ex)
                {
                    // DÜZELTME v26: Boş catch'e loglama eklendi
                    System.Diagnostics.Debug.WriteLine($"[PreviewService.StopAsync] CTS dispose hatası: {ex.Message}");
                }
                _cts = null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Event'i temizle
            OnFrame = null;

            // Güvenli senkron temizlik
            try
            {
                _cts?.Cancel();
                _previewTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                // DÜZELTME v26: Boş catch'e loglama eklendi
                System.Diagnostics.Debug.WriteLine($"[PreviewService.Dispose] Senkron temizlik hatası: {ex.Message}");
            }

            // Kaynakları temizle
            try { _frame?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[PreviewService.Dispose] Frame dispose hatası: {ex.Message}"); }
            try { _capture?.Release(); _capture?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[PreviewService.Dispose] Capture dispose hatası: {ex.Message}"); }
            try { _cts?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[PreviewService.Dispose] CTS dispose hatası: {ex.Message}"); }

            _frame = null;
            _capture = null;
            _cts = null;
            _previewTask = null;

            GC.SuppressFinalize(this);
        }
    }
}