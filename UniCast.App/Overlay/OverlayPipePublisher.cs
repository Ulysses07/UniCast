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

        private volatile bool _isDirty = true;
        private RenderTargetBitmap? _bmp;

        // --- PERFORMANS AYARLARI ---
        private DateTime _lastRender = DateTime.MinValue;
        private const int MIN_RENDER_INTERVAL_MS = 33; // ~30 FPS sınırı

        // --- RECONNECT AYARLARI ---
        private const int MAX_RECONNECT_ATTEMPTS = 10;
        private const int RECONNECT_DELAY_MS = 1000;

        public bool IsRunning { get; private set; }

        public OverlayPipePublisher(FrameworkElement visual, string pipeName, int width, int height)
        {
            _visual = visual ?? throw new ArgumentNullException(nameof(visual));
            _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
            _width = width > 0 ? width : 1280;
            _height = height > 0 ? height : 720;
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
        }

        /// <summary>
        /// Ana döngü - bağlantı koptuğunda otomatik yeniden bağlanır
        /// </summary>
        private async Task LoopWithReconnectAsync(CancellationToken ct)
        {
            int reconnectAttempts = 0;

            while (!ct.IsCancellationRequested && reconnectAttempts < MAX_RECONNECT_ATTEMPTS)
            {
                NamedPipeServerStream? server = null;

                try
                {
                    // Her döngüde yeni pipe oluştur
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.Out,
                        NamedPipeServerStream.MaxAllowedServerInstances, // Çoklu bağlantı desteği
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    System.Diagnostics.Debug.WriteLine($"[Overlay] Bağlantı bekleniyor... (Deneme: {reconnectAttempts + 1})");

                    // Bağlantı bekle (timeout ile)
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                    try
                    {
                        await server.WaitForConnectionAsync(timeoutCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Timeout - tekrar dene
                        System.Diagnostics.Debug.WriteLine("[Overlay] Bağlantı timeout, yeniden deneniyor...");
                        server.Dispose();
                        continue;
                    }

                    System.Diagnostics.Debug.WriteLine("[Overlay] Client bağlandı!");
                    reconnectAttempts = 0; // Başarılı bağlantıda sayacı sıfırla

                    // Aktif bağlantı döngüsü
                    await ProcessConnectionAsync(server, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Normal kapatma
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

                // Reconnect öncesi bekleme
                if (!ct.IsCancellationRequested && reconnectAttempts < MAX_RECONNECT_ATTEMPTS)
                {
                    var delay = RECONNECT_DELAY_MS * Math.Min(reconnectAttempts, 5); // Max 5x delay
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

            if (reconnectAttempts >= MAX_RECONNECT_ATTEMPTS)
            {
                System.Diagnostics.Debug.WriteLine("[Overlay] Maksimum yeniden bağlanma denemesi aşıldı!");
            }

            IsRunning = false;
        }

        /// <summary>
        /// Tek bir bağlantı için frame gönderme döngüsü
        /// </summary>
        private async Task ProcessConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && server.IsConnected)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var elapsed = (now - _lastRender).TotalMilliseconds;

                    // Sadece değişiklik varsa VE yeterli zaman geçtiyse çizim yap
                    if (_isDirty && elapsed >= MIN_RENDER_INTERVAL_MS)
                    {
                        byte[]? frameData = null;

                        // UI Thread üzerinde render al
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
                            // Frame boyutunu önce gönder (4 byte header)
                            var sizeHeader = BitConverter.GetBytes(frameData.Length);
                            await server.WriteAsync(sizeHeader, 0, sizeHeader.Length, ct);

                            // Frame verisini gönder
                            await server.WriteAsync(frameData, 0, frameData.Length, ct);
                            await server.FlushAsync(ct);

                            _isDirty = false;
                            _lastRender = now;
                        }
                    }

                    // İşlemciyi yormamak için kısa bir bekleme
                    await Task.Delay(10, ct);
                }
                catch (IOException)
                {
                    // Pipe koptu - döngüden çık, reconnect olacak
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
                // Lazy initialization
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