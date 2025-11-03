using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UniCast.App.Services;

public sealed class PreviewService
{
    public event Action<ImageSource>? OnFrame;

    private bool _running;
    private DispatcherTimer? _timer;

    public bool IsRunning => _running;

    public Task StartAsync(int width, int height, int fps)
    {
        if (_running) return Task.CompletedTask;

        // Cihaz var mı bak
        var s = SettingsStore.Load();
        bool hasCam = !string.IsNullOrWhiteSpace(s.DefaultCamera);

        _running = true;

        if (hasCam)
        {
            // Buraya mevcut gerçek kamera önizleme akışını koyabilirsiniz.
            // Şimdilik hızlı çözüm: kamera var fakat entegrasyon hazır değilse dahi pattern çiz.
            StartPatternTimer(width, height, fps);
        }
        else
        {
            // Kamera yoksa: test pattern
            StartPatternTimer(width, height, fps);
        }

        return Task.CompletedTask;
    }

    private void StartPatternTimer(int width, int height, int fps)
    {
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(10, 1000 / Math.Max(1, fps)))
        };

        int t = 0;
        _timer.Tick += (_, __) =>
        {
            var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            int stride = width * 4;
            var pixels = new byte[stride * height];

            // Basit hareketli renk bandı
            for (int y = 0; y < height; y++)
            {
                int offset = ((y + t) % height) * stride;
                for (int x = 0; x < width; x++)
                {
                    int i = offset + x * 4;
                    pixels[i + 0] = (byte)((x + t) % 256); // B
                    pixels[i + 1] = (byte)((y + t) % 256); // G
                    pixels[i + 2] = (byte)((x + y + t) % 256); // R
                    pixels[i + 3] = 255;
                }
            }

            wb.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, stride, 0);
            OnFrame?.Invoke(wb);
            t += 2;
        };
        _timer.Start();
    }

    public Task StopAsync()
    {
        _timer?.Stop();
        _timer = null;
        _running = false;
        return Task.CompletedTask;
    }
}
