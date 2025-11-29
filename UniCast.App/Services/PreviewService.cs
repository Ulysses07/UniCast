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
            while (!ct.IsCancellationRequested && _capture != null && _capture.IsOpened() && _frame != null)
            {
                try
                {
                    if (!_capture.Read(_frame) || _frame.Empty())
                    {
                        Thread.Sleep(10);
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

                    // DÜZELTME: Constants kullanımı
                    Thread.Sleep(Constants.Preview.FrameIntervalMs);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Preview Loop Error: {ex.Message}");
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
                catch { }
            }

            if (_previewTask != null)
            {
                try
                {
                    await _previewTask.ConfigureAwait(false);
                }
                catch { }
                _previewTask = null;
            }

            if (_frame != null)
            {
                try
                {
                    _frame.Dispose();
                }
                catch { }
                _frame = null;
            }

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

            // Güvenli senkron temizlik
            try
            {
                _cts?.Cancel();
                _previewTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch { }

            // Kaynakları temizle
            try { _frame?.Dispose(); } catch { }
            try { _capture?.Release(); _capture?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }

            _frame = null;
            _capture = null;
            _cts = null;
            _previewTask = null;

            GC.SuppressFinalize(this);
        }
    }
}