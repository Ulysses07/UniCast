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
    /// <summary>
    /// Overlay frame'lerini named pipe üzerinden FFmpeg'e gönderir.
    /// </summary>
    public sealed class OverlayPipePublisher : IAsyncDisposable
    {
        private readonly FrameworkElement _visual;
        private readonly string _pipeName;
        private readonly int _width;
        private readonly int _height;

        private CancellationTokenSource? _cts;
        private Task? _runner;

        private volatile bool _isDirty = true;
        private RenderTargetBitmap? _bmp;

        private DateTime _lastRender = DateTime.MinValue;

        public bool IsRunning { get; private set; }

        public OverlayPipePublisher(FrameworkElement visual, string pipeName, int width, int height)
        {
            _visual = visual ?? throw new ArgumentNullException(nameof(visual));
            _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
            _width = width > 0 ? width : Constants.Preview.DefaultWidth;
            _height = height > 0 ? height : Constants.Preview.DefaultHeight;
        }

        public void Invalidate()
        {
            _isDirty = true;
        }

        public void Start()
        {
            if (IsRunning) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            IsRunning = true;
            _runner = Task.Run(() => LoopWithReconnectAsync(_cts.Token));
        }

        public async Task StopAsync()
        {
            if (!IsRunning) return;

            try
            {
                _cts?.Cancel();
            }
            catch { }

            if (_runner != null)
            {
                try
                {
                    await _runner.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch { }
            }

            IsRunning = false;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _cts?.Dispose();
            _bmp = null;

            GC.SuppressFinalize(this);
        }

        private async Task LoopWithReconnectAsync(CancellationToken ct)
        {
            int reconnectAttempts = 0;

            while (!ct.IsCancellationRequested && reconnectAttempts < Constants.Overlay.MaxPipeReconnectAttempts)
            {
                NamedPipeServerStream? server = null;

                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.Out,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    System.Diagnostics.Debug.WriteLine($"[Overlay] Bağlantı bekleniyor... (Deneme: {reconnectAttempts + 1})");

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                    try
                    {
                        await server.WaitForConnectionAsync(timeoutCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        System.Diagnostics.Debug.WriteLine("[Overlay] Bağlantı timeout, yeniden deneniyor...");
                        server.Dispose();
                        continue;
                    }

                    System.Diagnostics.Debug.WriteLine("[Overlay] Client bağlandı!");
                    reconnectAttempts = 0;

                    await ProcessConnectionAsync(server, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Overlay] IO Hatası: {ex.Message}");
                    reconnectAttempts++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Overlay] Hata: {ex.Message}");
                    reconnectAttempts++;
                }
                finally
                {
                    try
                    {
                        server?.Dispose();
                    }
                    catch { }
                }

                if (!ct.IsCancellationRequested && reconnectAttempts < Constants.Overlay.MaxPipeReconnectAttempts)
                {
                    var delay = Constants.Overlay.PipeReconnectDelayMs * Math.Min(reconnectAttempts, 5);
                    System.Diagnostics.Debug.WriteLine($"[Overlay] {delay}ms sonra yeniden bağlanılacak...");

                    try
                    {
                        await Task.Delay(delay, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            if (reconnectAttempts >= Constants.Overlay.MaxPipeReconnectAttempts)
            {
                System.Diagnostics.Debug.WriteLine("[Overlay] Maksimum yeniden bağlanma denemesi aşıldı!");
            }

            IsRunning = false;
        }

        private async Task ProcessConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && server.IsConnected)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var elapsed = (now - _lastRender).TotalMilliseconds;

                    // DÜZELTME: Constants kullanımı
                    if (_isDirty && elapsed >= Constants.Overlay.MinRenderIntervalMs)
                    {
                        byte[]? frameData = null;

                        var app = Application.Current;
                        if (app != null)
                        {
                            await app.Dispatcher.InvokeAsync(() =>
                            {
                                frameData = RenderFrame();
                            });
                        }

                        if (frameData != null && frameData.Length > 0)
                        {
                            var sizeHeader = BitConverter.GetBytes(frameData.Length);
                            await server.WriteAsync(sizeHeader, 0, sizeHeader.Length, ct);
                            await server.WriteAsync(frameData, 0, frameData.Length, ct);
                            await server.FlushAsync(ct);

                            _isDirty = false;
                            _lastRender = now;
                        }
                    }

                    await Task.Delay(10, ct);
                }
                catch (IOException)
                {
                    System.Diagnostics.Debug.WriteLine("[Overlay] Bağlantı koptu");
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Overlay] Frame gönderme hatası: {ex.Message}");
                    break;
                }
            }
        }

        private byte[]? RenderFrame()
        {
            try
            {
                if (_bmp == null || _bmp.PixelWidth != _width || _bmp.PixelHeight != _height)
                {
                    _bmp = new RenderTargetBitmap(_width, _height, 96, 96, PixelFormats.Pbgra32);
                }

                _bmp.Clear();
                _bmp.Render(_visual);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(_bmp));

                using var ms = new MemoryStream();
                encoder.Save(ms);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Overlay] Render hatası: {ex.Message}");
                return null;
            }
        }
    }
}