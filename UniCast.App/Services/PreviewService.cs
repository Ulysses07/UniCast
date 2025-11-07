using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UniCast.App.Services
{
    /// <summary>
    /// AForge.Video.DirectShow ile kamera önizleme:
    /// - Cihazları listeler, öncelik sırasına göre açmayı dener (preferredIndex, Camo, DroidCam, diğerleri).
    /// - En yakın çözünürlük/fps profilini seçer.
    /// - NewFrame ile gelen Bitmap'i WPF ImageSource'a çevirir ve OnFrame olayıyla yayar.
    /// </summary>
    public sealed class PreviewService : IDisposable
    {
        public event Action<ImageSource>? OnFrame;

        private readonly object _sync = new();
        private AForge.Video.DirectShow.VideoCaptureDevice? _device;
        private CancellationTokenSource? _cts;
        private bool _isRunning;

        public bool IsRunning
        {
            get { lock (_sync) return _isRunning; }
            private set { lock (_sync) _isRunning = value; }
        }

        /// <summary>
        /// preferredIndex: Aygıt listesi indeksine öncelik (>=0 ise).
        /// width/height/fps: hedef profil (cihaz yeteneğine en yakın seçilir).
        /// </summary>
        public async Task StartAsync(int preferredIndex, int width, int height, int fps)
        {
            lock (_sync)
            {
                if (IsRunning) return;
                _cts = new CancellationTokenSource();
            }

            await Task.Run(() =>
            {
                var all = new AForge.Video.DirectShow.FilterInfoCollection(
                    AForge.Video.DirectShow.FilterCategory.VideoInputDevice);

                if (all.Count == 0)
                    throw new InvalidOperationException("Sistemde hiçbir video aygıtı bulunamadı.");

                // Öncelik sırası listesi
                var order = new System.Collections.Generic.List<int>();

                // 1) preferredIndex geçerliyse ekle
                if (preferredIndex >= 0 && preferredIndex < all.Count) order.Add(preferredIndex);

                // 2) “Camo” varsa ekle
                int camo = IndexOf(all, "Camo");
                if (camo >= 0 && !order.Contains(camo)) order.Add(camo);

                // 3) “DroidCam Video” varsa ekle
                int droid = IndexOf(all, "DroidCam Video");
                if (droid >= 0 && !order.Contains(droid)) order.Add(droid);

                // 4) Kalanları ekle
                for (int i = 0; i < all.Count; i++)
                    if (!order.Contains(i)) order.Add(i);

                Exception? last = null;

                foreach (int idx in order)
                {
                    try
                    {
                        OpenDevice(all[idx], width, height, fps);

                        IsRunning = true;

                        // Kapatma bekçisi
                        var token = _cts!.Token;
                        token.Register(() =>
                        {
                            try { SafeStop(); } catch { }
                        });

                        return; // başarı
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        SafeStop(); // yarım açılışları temizle
                    }
                }

                throw new InvalidOperationException(
                    $"Kamera açılamadı. Denenenler: {string.Join(", ", order.Select(i => $"{i}:{all[i].Name}"))}",
                    last
                );
            });
        }

        public async Task StopAsync()
        {
            CancellationTokenSource? cts;
            lock (_sync)
            {
                cts = _cts;
                _cts = null;
            }
            try { cts?.Cancel(); } catch { }

            await Task.Run(SafeStop);
            IsRunning = false;
        }

        private static int IndexOf(AForge.Video.DirectShow.FilterInfoCollection all, string name)
            => Enumerable.Range(0, all.Count)
                         .FirstOrDefault(i => all[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        private void OpenDevice(AForge.Video.DirectShow.FilterInfo info, int targetW, int targetH, int targetFps)
        {
            var dev = new AForge.Video.DirectShow.VideoCaptureDevice(info.MonikerString);

            // En yakın profile "snap"
            var caps = dev.VideoCapabilities;
            if (caps != null && caps.Length > 0)
            {
                var best = caps
                    .Select(c => new
                    {
                        Cap = c,
                        Score =
                            Math.Abs(c.FrameSize.Width - targetW) +
                            Math.Abs(c.FrameSize.Height - targetH) +
                            3 * Math.Abs(c.AverageFrameRate - targetFps)
                    })
                    .OrderBy(x => x.Score)
                    .First().Cap;

                dev.VideoResolution = best;
            }

            // Kare yakalama
            dev.NewFrame += (s, e) =>
            {
                try
                {
                    using var bmp = (System.Drawing.Bitmap)e.Frame.Clone();

                    // WPF ImageSource'a çevir
                    var img = ConvertToImageSource(bmp);
                    img.Freeze(); // UI thread'e marshalling gerekmesin

                    OnFrame?.Invoke(img);
                }
                catch
                {
                    // frame drop — yut
                }
            };

            dev.Start();

            // Açıldı mı?
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!dev.IsRunning)
            {
                if (sw.ElapsedMilliseconds > 3000)
                {
                    try { dev.SignalToStop(); dev.WaitForStop(); } catch { }
                    throw new InvalidOperationException($"Kamera açılamadı (name={info.Name}).");
                }
                Thread.Sleep(20);
            }

            lock (_sync) _device = dev;
        }

        private void SafeStop()
        {
            lock (_sync)
            {
                if (_device != null)
                {
                    try
                    {
                        if (_device.IsRunning)
                        {
                            _device.SignalToStop();
                            _device.WaitForStop();
                        }
                    }
                    catch { }
                    finally
                    {
                        try { _device.Stop(); } catch { }
                        _device = null;
                    }
                }
            }
        }

        private static BitmapSource ConvertToImageSource(System.Drawing.Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
            ms.Position = 0;

            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            return img;
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            SafeStop();
        }
    }
}
