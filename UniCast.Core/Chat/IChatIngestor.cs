using System;
using System.Threading;
using System.Threading.Tasks;

namespace UniCast.Core.Chat.Ingestors
{
    /// <summary>
    /// Chat ingestor arayüzü.
    /// Her platform için bir ingestor implemente eder.
    /// </summary>
    public interface IChatIngestor : IDisposable
    {
        /// <summary>
        /// Ingestor'ün bağlı olduğu platform.
        /// </summary>
        ChatPlatform Platform { get; }

        /// <summary>
        /// Bağlantı durumu.
        /// </summary>
        ConnectionState State { get; }

        /// <summary>
        /// Son hata mesajı.
        /// </summary>
        string? LastError { get; }

        /// <summary>
        /// Mesaj almayı başlatır.
        /// </summary>
        Task StartAsync(CancellationToken ct = default);

        /// <summary>
        /// Mesaj almayı durdurur.
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// Bağlantı durumu değişikliği event'i.
        /// </summary>
        event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    }

    /// <summary>
    /// Bağlantı durumları.
    /// </summary>
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
        Error
    }

    /// <summary>
    /// Bağlantı durumu değişikliği event argümanları.
    /// </summary>
    public sealed class ConnectionStateChangedEventArgs : EventArgs
    {
        public ConnectionState OldState { get; }
        public ConnectionState NewState { get; }
        public string? Message { get; }

        public ConnectionStateChangedEventArgs(ConnectionState oldState, ConnectionState newState, string? message = null)
        {
            OldState = oldState;
            NewState = newState;
            Message = message;
        }
    }
}