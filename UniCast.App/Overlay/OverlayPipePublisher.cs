using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UniCast.App.Overlay
{
    public sealed class OverlayPipePublisher : IAsyncDisposable
    {
        private readonly FrameworkElement _visual;
        private readonly string _pipeName;
        private readonly int _width;
        private readonly int _height;

        private const int TargetFps = 30;
        private readonly TimeSpan _frameInterval = TimeSpan.FromMilliseconds(1000.0 / TargetFps);

        private CancellationTokenSource? _cts;
        private Task? _runner;

        // Durum Yönetimi
        private bool _isDirty = true;
        private RenderTargetBitmap? _bmp;

        // UYARI DÜZELTME: '_encoder' alanı kullanılmıyordu, sildik.
        // private PngBitmapEncoder? _encoder; 

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
                    var startTime = DateTime.UtcNow;

                    if (_isDirty)
                    {
                        byte[]? frameData = null;

                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            frameData = RenderFrame();
                        });

                        if (frameData != null)
                        {
                            await server.WriteAsync(frameData, 0, frameData.Length, ct);
                            await server.FlushAsync(ct);
                            _isDirty = false;
                        }
                    }

                    var elapsed = DateTime.UtcNow - startTime;
                    var delay = _frameInterval - elapsed;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, ct);
                    }
                }
                catch (Exception)
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
                {
                    _bmp = new RenderTargetBitmap(_width, _height, 96, 96, PixelFormats.Pbgra32);
                }

                _bmp.Clear();
                _bmp.Render(_visual);

                // Encoder'ı yerel olarak oluşturuyoruz
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(_bmp));

                using var ms = new MemoryStream();
                encoder.Save(ms);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }
    }
}