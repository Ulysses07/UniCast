using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp; // AForge yerine bunu kullanıyoruz
using OpenCvSharp.WpfExtensions; // Bitmap dönüşümü için

namespace UniCast.App.Services
{
    public sealed class PreviewService : IDisposable
    {
        // Olaylar
        public event Action<ImageSource>? OnFrame;
        public bool IsRunning { get; private set; }

        // OpenCV Değişkenleri
        private VideoCapture? _capture;
        private Task? _previewTask;
        private CancellationTokenSource? _cts;

        public async Task StartAsync(int cameraIndex, int width, int height, int fps)
        {
            if (IsRunning) return;

            // Eğer kamera indeksi verilmemişse varsayılanı (0) dene
            if (cameraIndex < 0) cameraIndex = 0;

            try
            {
                // 1. Kamerayı Başlat (OpenCV)
                // CAP_DSHOW: Windows DirectShow backend'ini zorlar (Daha hızlı açılır)
                _capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);

                if (!_capture.IsOpened())
                {
                    System.Diagnostics.Debug.WriteLine("Preview: Kamera açılamadı.");
                    return;
                }

                // 2. Ayarları Uygula
                _capture.Set(VideoCaptureProperties.FrameWidth, width);
                _capture.Set(VideoCaptureProperties.FrameHeight, height);
                _capture.Set(VideoCaptureProperties.Fps, fps);

                // 3. Döngüyü Başlat
                _cts = new CancellationTokenSource();
                IsRunning = true;

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
            // Bellek sızıntısını önlemek için Mat nesnesini using ile yönetemeyiz (döngüdeyiz),
            // ama her karede yeniden oluşturmak yerine bir tane kullanıp doldurabiliriz.
            using var frame = new Mat();

            while (!ct.IsCancellationRequested && _capture != null && _capture.IsOpened())
            {
                try
                {
                    // Kare oku
                    if (!_capture.Read(frame) || frame.Empty())
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    // OpenCV Mat -> WPF WriteableBitmap dönüşümü
                    // OpenCvSharp.WpfExtensions paketi bu işi çok hızlı yapar.
                    var bmp = frame.ToWriteableBitmap();

                    // UI thread dışında oluşturulduğu için dondurmalıyız
                    bmp.Freeze();

                    // UI'a gönder
                    OnFrame?.Invoke(bmp);

                    // FPS kontrolü (kabaca)
                    Thread.Sleep(33);
                }
                catch
                {
                    // Hata olursa döngüden çıkma, bir sonraki kareyi dene
                }
            }
        }

        public async Task StopAsync()
        {
            if (!IsRunning) return;

            _cts?.Cancel();

            if (_previewTask != null)
            {
                try { await _previewTask.ConfigureAwait(false); } catch { }
            }

            // Kaynakları temizle
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;

            IsRunning = false;
        }

        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
            _cts?.Dispose();
        }
    }
}