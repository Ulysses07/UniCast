using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using static System.Net.Mime.MediaTypeNames;

namespace UniCast.Overlay
{
    /// <summary>
    /// Verilen WPF Visual'dan (UserControl) PNG (alpha) kareler üretip named pipe'a yazar.
    /// FFmpeg image2pipe ile okur.
    /// </summary>
    public sealed class OverlayPipePublisher : IAsyncDisposable
    {
        private readonly System.Windows.Controls.UserControl _visual;
        private readonly string _pipeName;
        private readonly int _fps;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public OverlayPipePublisher(System.Windows.Controls.UserControl visual, string pipeName = "unicast_overlay", int fps = 20)
        {
            _visual = visual;
            _pipeName = pipeName; // sadece isim, \\.\pipe\ ÖN EKİ YOK!
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
                    RenderTargetBitmap bmp = await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        int w = Math.Max(1, (int)Math.Ceiling(_visual.ActualWidth));
                        int h = Math.Max(1, (int)Math.Ceiling(_visual.ActualHeight));
                        if (w <= 0 || h <= 0) { w = 1280; h = 280; }

                        var rtb = new RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        _visual.Measure(new Size(w, h));
                        _visual.Arrange(new Rect(0, 0, w, h));
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
                    // FFmpeg kapanırsa pipe düşer: sessiz çık
                    break;
                }
            }
        }
    }
}
