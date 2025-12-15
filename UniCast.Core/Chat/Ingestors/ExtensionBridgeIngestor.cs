using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using UniCast.Core.Chat.Bridge;

namespace UniCast.Core.Chat.Ingestors
{
    /// <summary>
    /// Browser Extension üzerinden Instagram Live chat'i alan ingestor.
    /// WebView2 yerine kullanıcının kendi tarayıcısındaki extension'ı kullanır.
    /// 
    /// Avantajları:
    /// - Login sorunu yok (kullanıcı zaten giriş yapmış)
    /// - 2FA/CAPTCHA sorunu yok
    /// - Daha güvenilir ve hızlı
    /// </summary>
    public sealed class ExtensionBridgeIngestor : BaseChatIngestor
    {
        private ExtensionBridgeServer? _server;
        private readonly int _port;
        private bool _clientConnected;
        private TaskCompletionSource<bool>? _connectionTcs;

        public override ChatPlatform Platform => ChatPlatform.Instagram;

        /// <summary>
        /// Extension bağlı mı
        /// </summary>
        public bool IsExtensionConnected => _clientConnected;

        /// <summary>
        /// WebSocket server portu
        /// </summary>
        public int Port => _port;

        public ExtensionBridgeIngestor(int port = 9876) : base("extension-bridge")
        {
            _port = port;
        }

        protected override async Task ConnectAsync(CancellationToken ct)
        {
            Log.Information("[ExtensionBridge] Ingestor başlatılıyor, port: {Port}", _port);

            _server = new ExtensionBridgeServer(_port);

            // Event handler'ları bağla
            _server.OnMessageReceived += OnServerMessageReceived;
            _server.OnClientConnected += OnServerClientConnected;
            _server.OnClientDisconnected += OnServerClientDisconnected;

            // Server'ı başlat
            await _server.StartAsync();

            Log.Information("[ExtensionBridge] WebSocket server hazır. Extension bekleniyor...");
            Log.Information("[ExtensionBridge] Kullanıcıya: Instagram Live sayfasını tarayıcıda açın");
        }

        protected override async Task DisconnectAsync()
        {
            if (_server != null)
            {
                _server.OnMessageReceived -= OnServerMessageReceived;
                _server.OnClientConnected -= OnServerClientConnected;
                _server.OnClientDisconnected -= OnServerClientDisconnected;

                await _server.StopAsync();
                _server.Dispose();
                _server = null;
            }

            _clientConnected = false;
            Log.Information("[ExtensionBridge] Ingestor durduruldu");
        }

        protected override async Task RunMessageLoopAsync(CancellationToken ct)
        {
            // Extension push yapıyor, aktif polling gerekmiyor
            // Sadece cancellation token'ı bekle
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(1000, ct);

                    // Bağlantı durumunu kontrol et
                    if (_server != null && _server.ClientCount == 0 && _clientConnected)
                    {
                        _clientConnected = false;
                        Log.Warning("[ExtensionBridge] Extension bağlantısı koptu");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal iptal
            }
        }

        private void OnServerMessageReceived(ChatMessage message)
        {
            // Mesajı ChatBus'a ilet
            PublishMessage(message);
        }

        private void OnServerClientConnected(string clientId)
        {
            _clientConnected = true;
            Log.Information("[ExtensionBridge] Extension bağlandı: {ClientId}", clientId);
            _connectionTcs?.TrySetResult(true);
        }

        private void OnServerClientDisconnected(string clientId)
        {
            if (_server?.ClientCount == 0)
            {
                _clientConnected = false;
                Log.Warning("[ExtensionBridge] Tüm extension bağlantıları koptu");
            }
        }

        /// <summary>
        /// Extension bağlantısını bekle
        /// </summary>
        /// <param name="timeout">Maksimum bekleme süresi</param>
        public async Task<bool> WaitForConnectionAsync(TimeSpan timeout)
        {
            if (_clientConnected) return true;

            _connectionTcs = new TaskCompletionSource<bool>();

            using var cts = new CancellationTokenSource(timeout);
            cts.Token.Register(() => _connectionTcs.TrySetResult(false));

            return await _connectionTcs.Task;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _server?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}