using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using UniCast.Core.Chat;
using Application = System.Windows.Application;

namespace UniCast.App.Overlay
{
    /// <summary>
    /// Chat mesajlarını canlı yayın üzerine overlay olarak ekler.
    /// 
    /// Nasıl Çalışır:
    /// 1. ChatBus'tan gelen mesajları dinler
    /// 2. ChatOverlayRenderer ile frame render eder
    /// 3. Named Pipe üzerinden FFmpeg'e gönderir
    /// 4. FFmpeg overlay filter ile video üzerine composite eder
    /// 
    /// Kullanım:
    /// var service = new StreamChatOverlayService(1920, 1080);
    /// service.Start("unicast_chat_overlay");
    /// // ... streaming ...
    /// await service.StopAsync();
    /// </summary>
    public sealed class StreamChatOverlayService : IAsyncDisposable
    {
        #region Constants

        private const int DEFAULT_FPS = 30;
        private const string DEFAULT_PIPE_NAME = "unicast_chat_overlay";

        #endregion

        #region Fields

        private readonly ChatOverlayRenderer _renderer;
        private readonly string _pipeName;
        private readonly int _fps;
        private readonly int _frameDelayMs;

        private CancellationTokenSource? _cts;
        private Task? _pipeTask;
        private Task? _renderTask;

        private volatile bool _isRunning;
        private volatile bool _hasClient;
        private volatile byte[]? _currentFrame;
        private readonly object _frameLock = new();

        private bool _disposed;

        #endregion

        #region Properties

        /// <summary>Servis çalışıyor mu</summary>
        public bool IsRunning => _isRunning;

        /// <summary>FFmpeg bağlı mı</summary>
        public bool HasClient => _hasClient;

        /// <summary>Overlay genişliği</summary>
        public int Width => _renderer.Width;

        /// <summary>Overlay yüksekliği</summary>
        public int Height => _renderer.Height;

        /// <summary>Pipe adı (FFmpeg için)</summary>
        public string PipeName => _pipeName;

        /// <summary>Overlay ayarları</summary>
        public ChatOverlayRenderer Renderer => _renderer;

        #endregion

        #region Events

        /// <summary>FFmpeg bağlandığında</summary>
        public event EventHandler? ClientConnected;

        /// <summary>FFmpeg bağlantısı koptuğunda</summary>
        public event EventHandler? ClientDisconnected;

        /// <summary>Hata oluştuğunda</summary>
        public event EventHandler<Exception>? Error;

        #endregion

        #region Constructor

        /// <summary>
        /// Yeni StreamChatOverlayService oluştur
        /// </summary>
        /// <param name="width">Video genişliği</param>
        /// <param name="height">Video yüksekliği</param>
        /// <param name="pipeName">Named pipe adı</param>
        /// <param name="fps">Frame rate</param>
        public StreamChatOverlayService(int width, int height, string? pipeName = null, int fps = DEFAULT_FPS)
        {
            _renderer = new ChatOverlayRenderer(width, height);
            _pipeName = pipeName ?? DEFAULT_PIPE_NAME;
            _fps = fps;
            _frameDelayMs = 1000 / fps;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Servisi başlat ve ChatBus'a bağlan
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _isRunning = true;

            // ChatBus'a abone ol
            ChatBus.Instance.OnMerged += OnChatMessage;

            // Render loop başlat
            _renderTask = Task.Run(() => RenderLoopAsync(_cts.Token));

            // Pipe server başlat
            _pipeTask = Task.Run(() => PipeServerLoopAsync(_cts.Token));

            Log.Information("[StreamChatOverlay] Başlatıldı - Pipe: {PipeName}, {Width}x{Height}@{Fps}fps",
                _pipeName, Width, Height, _fps);
        }

        /// <summary>
        /// Servisi durdur
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning) return;

            _isRunning = false;

            // ChatBus aboneliğini kaldır
            ChatBus.Instance.OnMerged -= OnChatMessage;

            // İptal sinyali gönder
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException) { }

            // Task'ları bekle
            var tasks = new[] { _pipeTask, _renderTask }
                .Where(t => t != null)
                .ToArray();

            if (tasks.Length > 0)
            {
                try
                {
                    await Task.WhenAll(tasks!).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException) { }
                catch (TimeoutException)
                {
                    Log.Warning("[StreamChatOverlay] Stop timeout");
                }
            }

            _renderer.Clear();
            Log.Information("[StreamChatOverlay] Durduruldu");
        }

        /// <summary>
        /// Overlay boyutunu değiştir
        /// </summary>
        public void Resize(int width, int height)
        {
            _renderer.Resize(width, height);
            Log.Debug("[StreamChatOverlay] Boyut değişti: {Width}x{Height}", width, height);
        }

        /// <summary>
        /// FFmpeg args'a eklenecek overlay input parametresi
        /// </summary>
        public string GetFfmpegInputArgs()
        {
            return $"-f rawvideo -pix_fmt bgra -s {Width}x{Height} -r {_fps} -i \"\\\\.\\pipe\\{_pipeName}\"";
        }

        /// <summary>
        /// FFmpeg filter_complex için overlay parametresi
        /// </summary>
        public string GetFfmpegFilterArgs(string inputLabel, string overlayLabel, string outputLabel)
        {
            // Overlay'i alpha blending ile birleştir
            return $"[{inputLabel}][{overlayLabel}]overlay=0:0:eof_action=pass:format=auto[{outputLabel}]";
        }

        #endregion

        #region Private Methods

        private void OnChatMessage(ChatMessage message)
        {
            if (!_isRunning || _disposed) return;

            try
            {
                // UI thread'de çalıştır (WPF gereksinimi)
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    _renderer.AddMessage(message);
                });
            }
            catch (Exception ex)
            {
                Log.Debug("[StreamChatOverlay] Mesaj ekleme hatası: {Error}", ex.Message);
            }
        }

        private async Task RenderLoopAsync(CancellationToken ct)
        {
            Log.Debug("[StreamChatOverlay] Render loop başladı");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    byte[]? frame = null;

                    // UI thread'de render
                    await Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        frame = _renderer.RenderFrame();
                    })!;

                    if (frame != null)
                    {
                        lock (_frameLock)
                        {
                            _currentFrame = frame;
                        }
                    }

                    await Task.Delay(_frameDelayMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Debug("[StreamChatOverlay] Render hatası: {Error}", ex.Message);
                }
            }

            Log.Debug("[StreamChatOverlay] Render loop bitti");
        }

        private async Task PipeServerLoopAsync(CancellationToken ct)
        {
            Log.Debug("[StreamChatOverlay] Pipe server loop başladı");

            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;

                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.Out,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    Log.Debug("[StreamChatOverlay] FFmpeg bağlantısı bekleniyor...");

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
                        server.Dispose();
                        continue;
                    }

                    _hasClient = true;
                    Log.Information("[StreamChatOverlay] FFmpeg bağlandı!");
                    ClientConnected?.Invoke(this, EventArgs.Empty);

                    // Frame gönderme döngüsü
                    await SendFramesAsync(server, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException ex)
                {
                    Log.Debug("[StreamChatOverlay] Pipe IO hatası: {Error}", ex.Message);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[StreamChatOverlay] Pipe hatası");
                    Error?.Invoke(this, ex);
                }
                finally
                {
                    _hasClient = false;
                    ClientDisconnected?.Invoke(this, EventArgs.Empty);

                    try { server?.Dispose(); }
                    catch { }
                }

                // Yeniden bağlanmadan önce bekle
                if (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(1000, ct);
                    }
                    catch (OperationCanceledException) { break; }
                }
            }

            Log.Debug("[StreamChatOverlay] Pipe server loop bitti");
        }

        private async Task SendFramesAsync(NamedPipeServerStream server, CancellationToken ct)
        {
            var frameSize = Width * Height * 4; // BGRA
            var emptyFrame = new byte[frameSize];

            while (!ct.IsCancellationRequested && server.IsConnected)
            {
                try
                {
                    byte[]? frame;
                    lock (_frameLock)
                    {
                        frame = _currentFrame;
                    }

                    // Frame yoksa boş (şeffaf) frame gönder
                    var dataToSend = frame ?? emptyFrame;

                    if (dataToSend.Length == frameSize)
                    {
                        await server.WriteAsync(dataToSend, 0, dataToSend.Length, ct);
                        await server.FlushAsync(ct);
                    }

                    await Task.Delay(_frameDelayMs, ct);
                }
                catch (IOException)
                {
                    Log.Debug("[StreamChatOverlay] Bağlantı koptu");
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        #endregion

        #region IAsyncDisposable

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await StopAsync();

            _cts?.Dispose();
            _renderer.Dispose();

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}