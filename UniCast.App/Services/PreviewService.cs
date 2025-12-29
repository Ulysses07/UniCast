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
    /// DÜZELTME v52: Thread-safe lock ve düzgün toggle desteği
    /// DÜZELTME v57: NullReferenceException düzeltildi (thread-safe local copy)
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

        // Camera rotation (0, 90, 180, 270)
        private int _rotation = 0;

        // DÜZELTME v54: WriteableBitmap reuse - GC pressure azaltma
        private WriteableBitmap? _writeableBitmap;
        private int _bitmapWidth;
        private int _bitmapHeight;
        private byte[]? _copyBuffer;

        // DÜZELTME v52: Thread-safe lock
        private readonly SemaphoreSlim _operationLock = new(1, 1);
        private volatile bool _isStarting;
        private volatile bool _isStopping;

        public async Task StartAsync(int cameraIndex, int width, int height, int fps, int rotation = 0)
        {
            if (_disposed) return;

            // DÜZELTME v52: Aynı anda birden fazla start/stop çağrısını engelle
            if (_isStarting || _isStopping)
            {
                System.Diagnostics.Debug.WriteLine("[PreviewService] Başka bir işlem devam ediyor, bekleniyor...");
                await Task.Delay(100).ConfigureAwait(false);
                if (_isStarting || _isStopping) return;
            }

            bool lockAcquired = false;
            try
            {
                lockAcquired = await _operationLock.WaitAsync(3000).ConfigureAwait(false);
                if (!lockAcquired)
                {
                    System.Diagnostics.Debug.WriteLine("[PreviewService] Lock alınamadı, işlem iptal");
                    return;
                }

                _isStarting = true;

                // Zaten çalışıyorsa önce durdur
                if (IsRunning)
                {
                    System.Diagnostics.Debug.WriteLine("[PreviewService] Zaten çalışıyor, önce durduruluyor...");
                    await StopInternalAsync().ConfigureAwait(false);
                    await Task.Delay(200).ConfigureAwait(false);
                }

                if (cameraIndex < 0) cameraIndex = 0;
                _targetFps = fps > 0 ? fps : 30;
                _targetFrameTimeMs = 1000.0 / _targetFps;

                // Rotation değerini normalize et
                _rotation = rotation switch
                {
                    90 or -270 => 90,
                    180 or -180 => 180,
                    270 or -90 => 270,
                    _ => 0
                };
                System.Diagnostics.Debug.WriteLine($"[PreviewService] Camera rotation: {_rotation}°");

                // Eski kaynakları temizle
                CleanupCaptureInternal();

                System.Diagnostics.Debug.WriteLine($"[PreviewService] Kamera açılıyor: index={cameraIndex}, MSMF backend");

                // DÜZELTME v53: VideoCapture oluşturmayı background thread'de yap
                var captureResult = await Task.Run(() =>
                {
                    try
                    {
                        // MSMF backend
                        var capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.MSMF);

                        // MSMF başarısız olursa DSHOW'a fallback
                        if (capture == null || !capture.IsOpened())
                        {
                            System.Diagnostics.Debug.WriteLine("[PreviewService] MSMF başarısız, DSHOW deneniyor...");
                            capture?.Dispose();
                            Thread.Sleep(100);
                            capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
                        }

                        return capture;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PreviewService] VideoCapture oluşturma hatası: {ex.Message}");
                        return null;
                    }
                }).ConfigureAwait(false);

                _capture = captureResult;

                // DÜZELTME v57: Thread-safe local copy kullan
                var capture = _capture;
                if (capture == null || !capture.IsOpened())
                {
                    System.Diagnostics.Debug.WriteLine("[PreviewService] Kamera açılamadı - tüm backend'ler başarısız.");
                    CleanupCaptureInternal();
                    return;
                }

                // Kamera ayarları - local copy ile thread-safe
                capture.Set(VideoCaptureProperties.FrameWidth, width);
                capture.Set(VideoCaptureProperties.FrameHeight, height);
                capture.Set(VideoCaptureProperties.Fps, fps);
                capture.Set(VideoCaptureProperties.BufferSize, 1);

                // Gerçek değerleri kontrol et
                var actualFps = capture.Get(VideoCaptureProperties.Fps);
                var actualWidth = capture.Get(VideoCaptureProperties.FrameWidth);
                var actualHeight = capture.Get(VideoCaptureProperties.FrameHeight);

                // Geçersiz resolution kontrolü
                if (actualWidth < 1 || actualHeight < 1)
                {
                    System.Diagnostics.Debug.WriteLine($"[PreviewService] Geçersiz resolution: {actualWidth}x{actualHeight}, kamera kilitli olabilir");
                    CleanupCaptureInternal();
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"[PreviewService] Kamera açıldı: {actualWidth}x{actualHeight} @ {actualFps} FPS");

                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                _frame = new Mat();
                IsRunning = true;

                _previewTask = Task.Run(() => CaptureLoopOptimized(_cts.Token), _cts.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PreviewService] Start Error: {ex.Message}");
                CleanupCaptureInternal();
                IsRunning = false;
            }
            finally
            {
                _isStarting = false;
                if (lockAcquired)
                {
                    try { _operationLock.Release(); } catch { }
                }
            }
        }

        /// <summary>
        /// DÜZELTME v52: Internal cleanup - lock olmadan çağrılır
        /// DÜZELTME v54: WriteableBitmap temizliği eklendi
        /// </summary>
        private void CleanupCaptureInternal()
        {
            var capture = _capture;
            _capture = null;

            if (capture != null)
            {
                try
                {
                    if (capture.IsOpened())
                    {
                        capture.Release();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PreviewService] Release hatası: {ex.Message}");
                }

                try
                {
                    capture.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PreviewService] Dispose hatası: {ex.Message}");
                }
            }

            var frame = _frame;
            _frame = null;

            if (frame != null)
            {
                try { frame.Dispose(); } catch { }
            }

            // DÜZELTME v54: WriteableBitmap temizliği
            _writeableBitmap = null;
            _bitmapWidth = 0;
            _bitmapHeight = 0;
        }

        /// <summary>
        /// DÜZELTME v54: WriteableBitmap reuse ile GC pressure azaltıldı
        /// </summary>
        private void CaptureLoopOptimized(CancellationToken ct)
        {
            var stopwatch = new Stopwatch();

            while (!ct.IsCancellationRequested && IsRunning)
            {
                // DÜZELTME v57: Thread-safe local copy
                var capture = _capture;
                var frame = _frame;

                if (capture == null || frame == null)
                {
                    System.Diagnostics.Debug.WriteLine("[PreviewService] Capture veya frame null, loop sonlandırılıyor");
                    break;
                }

                // IsOpened kontrolü
                try
                {
                    if (!capture.IsOpened())
                    {
                        System.Diagnostics.Debug.WriteLine("[PreviewService] Capture artık açık değil, loop sonlandırılıyor");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PreviewService] IsOpened kontrolü hatası: {ex.Message}");
                    break;
                }

                stopwatch.Restart();

                try
                {
                    bool readSuccess = false;
                    try
                    {
                        readSuccess = capture.Read(frame);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PreviewService] Read Error: {ex.Message}");
                        break;
                    }

                    if (!readSuccess || frame.Empty())
                    {
                        Thread.Sleep(5);
                        continue;
                    }

                    // Rotation uygula (0 değilse)
                    if (_rotation != 0)
                    {
                        try
                        {
                            var rotateCode = _rotation switch
                            {
                                90 => RotateFlags.Rotate90Clockwise,
                                180 => RotateFlags.Rotate180,
                                270 => RotateFlags.Rotate90Counterclockwise,
                                _ => (RotateFlags?)null
                            };

                            if (rotateCode.HasValue)
                            {
                                Cv2.Rotate(frame, frame, rotateCode.Value);
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[PreviewService] Rotation Error: {ex.Message}");
                        }
                    }

                    try
                    {
                        // DÜZELTME v55: BeginInvoke kullan (non-blocking)
                        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Render,
                            new Action(() =>
                            {
                                try
                                {
                                    // DÜZELTME v57: frame hala geçerli mi kontrol et
                                    if (_frame != null && !_frame.Empty())
                                    {
                                        UpdateWriteableBitmap(_frame);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[PreviewService] UpdateWriteableBitmap Error: {ex.Message}");
                                }
                            }));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PreviewService] Dispatcher Error: {ex.Message}");
                    }

                    stopwatch.Stop();
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

            System.Diagnostics.Debug.WriteLine("[PreviewService] Capture loop sonlandı");
        }

        /// <summary>
        /// DÜZELTME v54: Mat'ı WriteableBitmap'e kopyala (reuse)
        /// </summary>
        private void UpdateWriteableBitmap(Mat frame)
        {
            if (frame == null || frame.Empty()) return;

            int width = frame.Width;
            int height = frame.Height;
            var sourceStride = (int)frame.Step();
            int totalBytes = sourceStride * height;

            // Geçersiz boyut kontrolü
            if (width <= 0 || height <= 0 || totalBytes <= 0)
            {
                return;
            }

            // WriteableBitmap boyutu değiştiyse veya yoksa yeniden oluştur
            if (_writeableBitmap == null || _bitmapWidth != width || _bitmapHeight != height)
            {
                _writeableBitmap = new WriteableBitmap(
                    width, height,
                    96, 96,
                    System.Windows.Media.PixelFormats.Bgr24,
                    null);
                _bitmapWidth = width;
                _bitmapHeight = height;
                _copyBuffer = null;
                System.Diagnostics.Debug.WriteLine($"[PreviewService] WriteableBitmap oluşturuldu: {width}x{height}");
            }

            // Buffer boyutu yetmiyorsa yeniden oluştur
            if (_copyBuffer == null || _copyBuffer.Length < totalBytes)
            {
                _copyBuffer = new byte[totalBytes];
            }

            try
            {
                _writeableBitmap.Lock();

                var sourcePtr = frame.Data;
                var destPtr = _writeableBitmap.BackBuffer;
                var stride = _writeableBitmap.BackBufferStride;

                // Pointer geçerlilik kontrolü
                if (sourcePtr == IntPtr.Zero || destPtr == IntPtr.Zero)
                {
                    _writeableBitmap.Unlock();
                    return;
                }

                // Mat'tan buffer'a kopyala
                System.Runtime.InteropServices.Marshal.Copy(sourcePtr, _copyBuffer, 0, totalBytes);

                // Buffer'dan WriteableBitmap'e kopyala
                if (stride == sourceStride)
                {
                    System.Runtime.InteropServices.Marshal.Copy(_copyBuffer, 0, destPtr, totalBytes);
                }
                else
                {
                    // Stride farklıysa satır satır kopyala
                    for (int y = 0; y < height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(
                            _copyBuffer,
                            y * sourceStride,
                            destPtr + y * stride,
                            Math.Min(stride, sourceStride));
                    }
                }

                _writeableBitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, width, height));
            }
            finally
            {
                _writeableBitmap.Unlock();
            }

            // Event'i tetikle
            OnFrame?.Invoke(_writeableBitmap);
        }

        public async Task StopAsync()
        {
            if (!IsRunning && _capture == null && _previewTask == null) return;

            // DÜZELTME v52: Zaten durduruluyorsa bekle
            if (_isStopping)
            {
                System.Diagnostics.Debug.WriteLine("[PreviewService] Zaten durduruluyor, bekleniyor...");
                await Task.Delay(100).ConfigureAwait(false);
                return;
            }

            bool lockAcquired = false;
            try
            {
                lockAcquired = await _operationLock.WaitAsync(3000).ConfigureAwait(false);
                if (!lockAcquired)
                {
                    System.Diagnostics.Debug.WriteLine("[PreviewService] Stop için lock alınamadı");
                    return;
                }

                await StopInternalAsync().ConfigureAwait(false);
            }
            finally
            {
                if (lockAcquired)
                {
                    try { _operationLock.Release(); } catch { }
                }
            }
        }

        /// <summary>
        /// DÜZELTME v52: Internal stop - lock olmadan çağrılır
        /// </summary>
        private async Task StopInternalAsync()
        {
            _isStopping = true;
            IsRunning = false;

            try
            {
                // CTS'i iptal et
                var cts = _cts;
                if (cts != null)
                {
                    try { cts.Cancel(); } catch { }
                }

                // Task'ın bitmesini bekle
                var task = _previewTask;
                if (task != null)
                {
                    try
                    {
                        await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        System.Diagnostics.Debug.WriteLine("[PreviewService] Task timeout, devam ediliyor");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PreviewService] Task bekleme hatası: {ex.Message}");
                    }
                }
                _previewTask = null;

                // Kaynakları temizle
                CleanupCaptureInternal();

                // CTS'i dispose et
                if (cts != null)
                {
                    try { cts.Dispose(); } catch { }
                }
                _cts = null;

                System.Diagnostics.Debug.WriteLine("[PreviewService] Stop tamamlandı");
            }
            finally
            {
                _isStopping = false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            OnFrame = null;
            IsRunning = false;

            try
            {
                _cts?.Cancel();
                _previewTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch { }

            CleanupCaptureInternal();

            try { _cts?.Dispose(); } catch { }
            try { _operationLock.Dispose(); } catch { }

            _cts = null;
            _previewTask = null;

            GC.SuppressFinalize(this);
        }
    }
}