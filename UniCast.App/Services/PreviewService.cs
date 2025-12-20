using System;
using System.Diagnostics;
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
    /// DÜZELTME v50: FPS optimizasyonu
    /// - MSMF backend (DSHOW yerine - daha hızlı)
    /// - Dinamik frame timing (işleme süresini hesaba katar)
    /// - Buffer optimizasyonu
    /// </summary>
    public sealed class PreviewService : IPreviewService
    {
        public event Action<ImageSource>? OnFrame;
        public bool IsRunning { get; private set; }

        private VideoCapture? _capture;
        private Task? _previewTask;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        private Mat? _frame;

        // FPS tracking
        private int _targetFps = 30;
        private double _targetFrameTimeMs = 33.33;

        public async Task StartAsync(int cameraIndex, int width, int height, int fps)
        {
            if (IsRunning || _disposed) return;

            if (cameraIndex < 0) cameraIndex = 0;
            _targetFps = fps > 0 ? fps : 30;
            _targetFrameTimeMs = 1000.0 / _targetFps;

            try
            {
                // DÜZELTME v50: MSMF backend kullan (Windows Media Foundation - daha hızlı)
                _capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.MSMF);

                // MSMF başarısız olursa DSHOW'a fallback
                if (!_capture.IsOpened())
                {
                    _capture.Dispose();
                    _capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
                }

                if (!_capture.IsOpened())
                {
                    System.Diagnostics.Debug.WriteLine("[PreviewService] Kamera açılamadı.");
                    return;
                }

                // Kamera ayarları
                _capture.Set(VideoCaptureProperties.FrameWidth, width);
                _capture.Set(VideoCaptureProperties.FrameHeight, height);
                _capture.Set(VideoCaptureProperties.Fps, fps);

                // DÜZELTME v50: Buffer ayarları - düşük latency için
                _capture.Set(VideoCaptureProperties.BufferSize, 1);

                // Gerçek FPS'i logla
                var actualFps = _capture.Get(VideoCaptureProperties.Fps);
                var actualWidth = _capture.Get(VideoCaptureProperties.FrameWidth);
                var actualHeight = _capture.Get(VideoCaptureProperties.FrameHeight);
                System.Diagnostics.Debug.WriteLine($"[PreviewService] Kamera açıldı: {actualWidth}x{actualHeight} @ {actualFps} FPS");

                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                IsRunning = true;

                _frame = new Mat();

                _previewTask = Task.Run(() => CaptureLoopOptimized(_cts.Token), _cts.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PreviewService] Start Error: {ex.Message}");
                await StopAsync();
            }
        }

        /// <summary>
        /// DÜZELTME v50: Optimize edilmiş capture loop
        /// - Dinamik timing (işleme süresini hesaba katar)
        /// - Daha az allocation
        /// </summary>
        private void CaptureLoopOptimized(CancellationToken ct)
        {
            var stopwatch = new Stopwatch();

            while (!ct.IsCancellationRequested && _capture != null && _capture.IsOpened() && _frame != null)
            {
                stopwatch.Restart();

                try
                {
                    // Frame oku
                    bool readSuccess = false;
                    try
                    {
                        readSuccess = _capture.Read(_frame);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PreviewService] Read Error: {ex.Message}");
                    }

                    if (!readSuccess || _frame.Empty())
                    {
                        Thread.Sleep(5);
                        continue;
                    }

                    // Frame'i WPF'e çevir
                    try
                    {
                        var bmp = _frame.ToWriteableBitmap();
                        bmp.Freeze();
                        OnFrame?.Invoke(bmp);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PreviewService] Frame Convert Error: {ex.Message}");
                    }

                    stopwatch.Stop();

                    // DÜZELTME v50: Dinamik delay - işleme süresini çıkar
                    var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                    var remainingMs = _targetFrameTimeMs - elapsedMs;

                    if (remainingMs > 1)
                    {
                        Thread.Sleep((int)remainingMs);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PreviewService] Loop Error: {ex.Message}");
                    Thread.Sleep(50);
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