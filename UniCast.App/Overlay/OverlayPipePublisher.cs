using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;

namespace UniCast.App.Overlay
{
    public sealed class OverlayPipePublisher : IAsyncDisposable
    {
        private readonly FrameworkElement _visual;
        private readonly string _pipeName;
        private readonly int _width;
        private readonly int _height;

        private CancellationTokenSource? _cts;
        private Task? _runner;

        private bool _isDirty = true;
        private RenderTargetBitmap? _bmp;

        // --- PERFORMANS AYARLARI (YENİ EKLENDİ) ---
        private DateTime _lastRender = DateTime.MinValue;
        private const int MIN_RENDER_INTERVAL_MS = 33; // ~30 FPS sınırı

        public bool IsRunning { get; private set; }

        public OverlayPipePublisher(FrameworkElement visual, string pipeName, int width, int height)
        {
            _visual = visual;
            _pipeName = pipeName;
            _width = width;
            _height = height;
        }

        public void Invalidate()
        {
            _isDirty = true;
        }

        public void Start()
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            IsRunning = true;
            _runner = Task.Run(() => LoopAsync(_cts.Token));
        }

        public async Task StopAsync()
        {
            if (!IsRunning) return;
            _cts?.Cancel();
            if (_runner != null)
            {
                try { await _runner.ConfigureAwait(false); } catch { }
            }
            IsRunning = false;
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        private async Task LoopAsync(CancellationToken ct)
        {
            using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(ct);
            }
            catch
            {
                return;
            }

            while (!ct.IsCancellationRequested && server.IsConnected)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var elapsed = (now - _lastRender).TotalMilliseconds;

                    // --- PERFORMANS OPTİMİZASYONU ---
                    // Sadece değişiklik varsa (isDirty) VE yeterli zaman geçtiyse (33ms) çizim yap.
                    if (_isDirty && elapsed >= MIN_RENDER_INTERVAL_MS)
                    {
                        byte[]? frameData = null;

                        // UI Thread üzerinde render al
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            frameData = RenderFrame();
                        });

                        if (frameData != null)
                        {
                            await server.WriteAsync(frameData, 0, frameData.Length, ct);
                            await server.FlushAsync(ct);

                            _isDirty = false;
                            _lastRender = now; // Son çizim zamanını güncelle
                        }
                    }

                    // İşlemciyi yormamak için kısa bir bekleme
                    await Task.Delay(10, ct);
                }
                catch
                {
                    break;
                }
            }
        }

        private byte[]? RenderFrame()
        {
            try
            {
                if (_bmp == null)
                    _bmp = new RenderTargetBitmap(_width, _height, 96, 96, PixelFormats.Pbgra32);

                _bmp.Clear();
                _bmp.Render(_visual);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(_bmp));

                using var ms = new MemoryStream();
                encoder.Save(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }
    }
}