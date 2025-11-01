using System;
using System.Threading;
using System.Threading.Tasks;

namespace UniCast.Core.Chat
{
    public interface IChatIngestor : IAsyncDisposable
    {
        event Action<ChatMessage>? OnMessage;
        Task StartAsync(CancellationToken ct);
        Task StopAsync();
        string Name { get; }
        bool IsRunning { get; }
    }
}
