using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace UniCast.App.Overlay
{
    /// <summary>
    /// Verilen WPF FrameworkElement'ten periyodik PNG (alpha) kare üretip named pipe'a yazar.
    /// FFmpeg: -f image2pipe -r 20 -i \\.\pipe\unicast_overlay
    /// </summary>
    public sealed class OverlayPipePublisher : IAsyncDisposable
    {
        private readonly FrameworkElement _visual;
        private readonly string _pipeName;
        private readonly int _fps;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public OverlayPipePublisher(FrameworkElement visual, string pipeName = "unicast_overlay", int fps = 20)
        {
            _visual = visual;
            _pipeName = pipeName; // sadece isim; client: \\.\pipe\{name}
            _fps = Math.Clamp(fps, 5, 60);
        }

        public void Start()
        {
            if (_loop is not null) return;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        public async Task StopAsync()
        {
            try { _cts?.Cancel(); } catch { }
            if (_loop is not null) { try { await _loop; } catch { } }
            _loop = null;
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        private async Task RunAsync(CancellationToken ct)
        {
            using var server = new NamedPipeServerStream(_pipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024 * 1024, 1024 * 1024);

            await server.WaitForConnectionAsync(ct);

            var frameDelay = TimeSpan.FromMilliseconds(1000.0 / _fps);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var bmp = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        int w = Math.Max(1, (int)Math.Ceiling(_visual.ActualWidth));
                        int h = Math.Max(1, (int)Math.Ceiling(_visual.ActualHeight));
                        if (w <= 0 || h <= 0) { w = 1280; h = 280; }

                        var rtb = new RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        _visual.Measure(new System.Windows.Size(w, h));
                        _visual.Arrange(new Rect(new System.Windows.Point(0, 0), _visual.DesiredSize));
                        _visual.UpdateLayout();
                        rtb.Render(_visual);
                        return rtb;
                    });

                    using var ms = new MemoryStream();
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    enc.Save(ms);

                    ms.Position = 0;
                    await ms.CopyToAsync(server, ct);
                    await server.FlushAsync(ct);

                    await Task.Delay(frameDelay, ct);
                }
                catch (OperationCanceledException) { }
                catch
                {
                    // FFmpeg kapanırsa pipe düşer – sessiz çık
                    break;
                }
            }
        }
    }
}
