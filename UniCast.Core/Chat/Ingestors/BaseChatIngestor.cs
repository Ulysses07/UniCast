using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.Core.Chat.Ingestors
{
    /// <summary>
    /// Chat ingestor'lar için temel sınıf.
    /// Ortak işlevselliği sağlar.
    /// </summary>
    public abstract class BaseChatIngestor : IChatIngestor
    {
        protected readonly string _identifier;
        protected CancellationTokenSource? _cts;
        protected Task? _runningTask;

        private ConnectionState _state = ConnectionState.Disconnected;
        private string? _lastError;
        private bool _disposed;

        public abstract ChatPlatform Platform { get; }

        public ConnectionState State
        {
            get => _state;
            protected set
            {
                if (_state != value)
                {
                    var oldState = _state;
                    _state = value;
                    OnStateChanged(oldState, value);
                }
            }
        }

        public string? LastError
        {
            get => _lastError;
            protected set => _lastError = value;
        }

        public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

        protected BaseChatIngestor(string identifier)
        {
            _identifier = identifier ?? throw new ArgumentNullException(nameof(identifier));
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();

            if (State == ConnectionState.Connected || State == ConnectionState.Connecting)
            {
                Log.Warning("[{Platform}] Zaten bağlı veya bağlanıyor", Platform);
                return;
            }

            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            State = ConnectionState.Connecting;
            LastError = null;

            try
            {
                Log.Information("[{Platform}] Bağlanılıyor: {Identifier}", Platform, _identifier);

                await ConnectAsync(_cts.Token);

                State = ConnectionState.Connected;
                Log.Information("[{Platform}] Bağlandı: {Identifier}", Platform, _identifier);

                // Mesaj alma döngüsünü başlat
                _runningTask = RunMessageLoopAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                State = ConnectionState.Disconnected;
                Log.Information("[{Platform}] Bağlantı iptal edildi", Platform);
            }
            catch (Exception ex)
            {
                State = ConnectionState.Error;
                LastError = ex.Message;
                Log.Error(ex, "[{Platform}] Bağlantı hatası", Platform);
                throw;
            }
        }

        public async Task StopAsync()
        {
            if (State == ConnectionState.Disconnected)
                return;

            Log.Information("[{Platform}] Durduruluyor: {Identifier}", Platform, _identifier);

            try
            {
                _cts?.Cancel();

                if (_runningTask != null)
                {
                    try
                    {
                        await _runningTask.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (TimeoutException)
                    {
                        Log.Warning("[{Platform}] Durdurma timeout", Platform);
                    }
                    catch (OperationCanceledException)
                    {
                        // Beklenen
                    }
                }

                await DisconnectAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[{Platform}] Durdurma hatası", Platform);
            }
            finally
            {
                State = ConnectionState.Disconnected;
                _runningTask = null;
            }
        }

        /// <summary>
        /// Platforma bağlanır. Alt sınıfta implemente edilmeli.
        /// </summary>
        protected abstract Task ConnectAsync(CancellationToken ct);

        /// <summary>
        /// Platform bağlantısını keser. Alt sınıfta implemente edilmeli.
        /// </summary>
        protected abstract Task DisconnectAsync();

        /// <summary>
        /// Mesaj alma döngüsü. Alt sınıfta implemente edilmeli.
        /// </summary>
        protected abstract Task RunMessageLoopAsync(CancellationToken ct);

        /// <summary>
        /// ChatBus'a mesaj yayınlar.
        /// </summary>
        protected void PublishMessage(ChatMessage message)
        {
            if (_disposed)
                return;

            try
            {
                ChatBus.Instance.Publish(message);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[{Platform}] Mesaj yayınlama hatası", Platform);
            }
        }

        /// <summary>
        /// Yeniden bağlanma mantığı.
        /// </summary>
        protected async Task ReconnectAsync(CancellationToken ct, int maxAttempts = 5, int baseDelayMs = 1000)
        {
            State = ConnectionState.Reconnecting;

            for (int attempt = 1; attempt <= maxAttempts && !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    var delay = baseDelayMs * (int)Math.Pow(2, attempt - 1); // Exponential backoff
                    Log.Information("[{Platform}] Yeniden bağlanma denemesi {Attempt}/{Max}, bekle {Delay}ms",
                        Platform, attempt, maxAttempts, delay);

                    await Task.Delay(delay, ct);
                    await ConnectAsync(ct);

                    State = ConnectionState.Connected;
                    Log.Information("[{Platform}] Yeniden bağlantı başarılı", Platform);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[{Platform}] Yeniden bağlanma denemesi {Attempt} başarısız",
                        Platform, attempt);
                    LastError = ex.Message;
                }
            }

            State = ConnectionState.Error;
            throw new Exception($"Maksimum yeniden bağlanma denemesi ({maxAttempts}) aşıldı");
        }

        private void OnStateChanged(ConnectionState oldState, ConnectionState newState)
        {
            try
            {
                StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(oldState, newState, LastError));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[{Platform}] StateChanged event hatası", Platform);
            }
        }

        protected void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                try
                {
                    _cts?.Cancel();
                    _cts?.Dispose();
                }
                catch (Exception ex)
                {
                    // DÜZELTME v26: Boş catch'e loglama eklendi
                    System.Diagnostics.Debug.WriteLine($"[BaseChatIngestor.Dispose] CTS temizleme hatası: {ex.Message}");
                }

                StateChanged = null;
            }

            _disposed = true;
        }

        ~BaseChatIngestor()
        {
            Dispose(false);
        }
    }
}