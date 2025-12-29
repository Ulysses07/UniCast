using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace UniCast.App.Services
{
    /// <summary>
    /// Named Pipe üzerinden FFmpeg'e raw video frame'leri gönderir.
    /// Bu sayede kamera sadece OpenCV tarafından açılır ve FFmpeg pipe'dan okur.
    /// </summary>
    public sealed class FramePipeWriter : IDisposable
    {
        public const string DefaultPipeName = "unicast_video_pipe";

        private NamedPipeServerStream? _pipeServer;
        private readonly string _pipeName;
        private bool _isConnected;
        private bool _disposed;
        private readonly object _writeLock = new();

        // Frame bilgileri
        private int _width;
        private int _height;
        private int _fps;

        public bool IsConnected => _isConnected;
        public string PipeName => _pipeName;
        public int Width => _width;
        public int Height => _height;
        public int Fps => _fps;

        public FramePipeWriter(string pipeName = DefaultPipeName)
        {
            _pipeName = pipeName;
        }

        /// <summary>
        /// Pipe sunucusunu başlat ve FFmpeg'in bağlanmasını bekle
        /// </summary>
        public async Task StartAsync(int width, int height, int fps, CancellationToken ct = default)
        {
            if (_disposed) return;

            _width = width;
            _height = height;
            _fps = fps;

            try
            {
                // Eski pipe varsa temizle
                Stop();

                System.Diagnostics.Debug.WriteLine($"[FramePipeWriter] Pipe oluşturuluyor: {_pipeName}");

                _pipeServer = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.Out,
                    1, // maxServerInstances
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,  // inBufferSize (okuma yapmıyoruz)
                    width * height * 3 * 2);  // outBufferSize (2 frame buffer)

                System.Diagnostics.Debug.WriteLine("[FramePipeWriter] FFmpeg bağlantısı bekleniyor...");

                // FFmpeg bağlanana kadar bekle (timeout ile)
                var connectTask = _pipeServer.WaitForConnectionAsync(ct);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), ct);

                var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    System.Diagnostics.Debug.WriteLine("[FramePipeWriter] FFmpeg bağlantı zaman aşımı!");
                    Stop();
                    return;
                }

                await connectTask; // Exception varsa fırlat

                _isConnected = true;
                System.Diagnostics.Debug.WriteLine("[FramePipeWriter] FFmpeg bağlandı!");
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("[FramePipeWriter] Bağlantı iptal edildi");
                Stop();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FramePipeWriter] Başlatma hatası: {ex.Message}");
                Stop();
            }
        }

        /// <summary>
        /// OpenCV Mat frame'ini pipe'a yaz
        /// </summary>
        public bool WriteFrame(Mat frame)
        {
            if (!_isConnected || _pipeServer == null || !_pipeServer.IsConnected || frame == null || frame.Empty())
                return false;

            lock (_writeLock)
            {
                try
                {
                    // Mat'ı BGR24 formatında byte array'e dönüştür
                    // FFmpeg rawvideo için: bgr24, width*height*3 bytes per frame

                    int dataSize = frame.Width * frame.Height * frame.Channels();

                    // Frame pointer'dan direkt kopyala
                    byte[] buffer = new byte[dataSize];
                    System.Runtime.InteropServices.Marshal.Copy(frame.Data, buffer, 0, dataSize);

                    _pipeServer.Write(buffer, 0, buffer.Length);
                    _pipeServer.Flush();

                    return true;
                }
                catch (IOException ex)
                {
                    // Pipe koptu - FFmpeg kapandı
                    System.Diagnostics.Debug.WriteLine($"[FramePipeWriter] Pipe yazma hatası: {ex.Message}");
                    _isConnected = false;
                    return false;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FramePipeWriter] Frame yazma hatası: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Pipe'ı kapat
        /// </summary>
        public void Stop()
        {
            _isConnected = false;

            try
            {
                if (_pipeServer != null)
                {
                    if (_pipeServer.IsConnected)
                    {
                        _pipeServer.Disconnect();
                    }
                    _pipeServer.Dispose();
                    _pipeServer = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FramePipeWriter] Stop hatası: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine("[FramePipeWriter] Pipe kapatıldı");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
        }
    }
}