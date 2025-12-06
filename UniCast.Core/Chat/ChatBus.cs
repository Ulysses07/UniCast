using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.Core.Chat
{
    /// <summary>
    /// Merkezi chat mesaj dağıtım sistemi.
    /// Tüm platformlardan gelen mesajları birleştirir ve dağıtır.
    /// Thread-safe ve memory leak güvenli.
    /// </summary>
    public sealed class ChatBus : IDisposable
    {
        private static readonly Lazy<ChatBus> _instance = new(() => new ChatBus(), LazyThreadSafetyMode.ExecutionAndPublication);
        public static ChatBus Instance => _instance.Value;

        // Event (weak reference pattern önerilir ama basitlik için standard kullanıyoruz)
        public event EventHandler<ChatMessageEventArgs>? MessageReceived;

        /// <summary>
        /// Mesaj birleştirildiğinde tetiklenir (ChatViewModel için).
        /// </summary>
        public event Action<ChatMessage>? OnMerged;

        // Message queue (rate limiting için)
        private readonly ConcurrentQueue<ChatMessage> _messageQueue = new();
        private readonly SemaphoreSlim _processingLock = new(1, 1);

        // Rate limiting
        private readonly ConcurrentDictionary<string, DateTime> _lastMessageTime = new();
        private const int MinMessageIntervalMs = 100; // Platform başına minimum mesaj aralığı

        // Statistics
        private long _totalMessagesReceived;
        private long _totalMessagesDropped;

        private bool _disposed;

        private ChatBus()
        {
            Log.Debug("[ChatBus] Initialized");
        }

        /// <summary>
        /// Yeni bir chat mesajı yayınlar.
        /// </summary>
        public void Publish(ChatMessage message)
        {
            if (_disposed)
                return;

            if (message == null)
                return;

            Interlocked.Increment(ref _totalMessagesReceived);
            Log.Debug("[ChatBus] Mesaj alındı: {Platform} - {User}: {Content}", message.Platform, message.DisplayName, message.Message);

            // Rate limiting kontrolü - Kullanıcı + Platform bazlı (aynı kullanıcıdan spam önleme)
            var userKey = $"{message.Platform}:{message.Username}";
            var now = DateTime.UtcNow;

            if (_lastMessageTime.TryGetValue(userKey, out var lastTime))
            {
                if ((now - lastTime).TotalMilliseconds < MinMessageIntervalMs)
                {
                    Interlocked.Increment(ref _totalMessagesDropped);
                    Log.Verbose("[ChatBus] Message rate limited: {User} on {Platform}", message.Username, message.Platform);
                    return;
                }
            }

            _lastMessageTime[userKey] = now;

            // Memory leak önleme: Dictionary çok büyürse eski entry'leri temizle
            if (_lastMessageTime.Count > 5000)
            {
                CleanupOldEntries();
            }

            // Event'i tetikle
            try
            {
                Log.Debug("[ChatBus] Event tetikleniyor - Subscribers: MessageReceived={MR}, OnMerged={OM}",
                    MessageReceived != null, OnMerged != null);

                MessageReceived?.Invoke(this, new ChatMessageEventArgs(message));
                OnMerged?.Invoke(message);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ChatBus] MessageReceived event handler hatası");
            }
        }

        /// <summary>
        /// 5 dakikadan eski rate limit entry'lerini temizler.
        /// </summary>
        private void CleanupOldEntries()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-5);
            var keysToRemove = _lastMessageTime
                .Where(kvp => kvp.Value < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _lastMessageTime.TryRemove(key, out _);
            }

            Log.Debug("[ChatBus] Rate limit cache temizlendi: {Removed} entry silindi, {Remaining} kaldı",
                keysToRemove.Count, _lastMessageTime.Count);
        }

        /// <summary>
        /// Async olarak mesaj yayınlar.
        /// </summary>
        public async Task PublishAsync(ChatMessage message, CancellationToken ct = default)
        {
            if (_disposed || ct.IsCancellationRequested)
                return;

            await Task.Run(() => Publish(message), ct);
        }

        /// <summary>
        /// Toplu mesaj yayınlar.
        /// </summary>
        public void PublishBatch(IEnumerable<ChatMessage> messages)
        {
            if (_disposed)
                return;

            foreach (var message in messages)
            {
                Publish(message);
            }
        }

        /// <summary>
        /// İstatistikleri döndürür.
        /// </summary>
        public ChatBusStatistics GetStatistics()
        {
            return new ChatBusStatistics
            {
                TotalMessagesReceived = Interlocked.Read(ref _totalMessagesReceived),
                TotalMessagesDropped = Interlocked.Read(ref _totalMessagesDropped),
                ActivePlatforms = _lastMessageTime.Count
            };
        }

        /// <summary>
        /// İstatistikleri sıfırlar.
        /// </summary>
        public void ResetStatistics()
        {
            Interlocked.Exchange(ref _totalMessagesReceived, 0);
            Interlocked.Exchange(ref _totalMessagesDropped, 0);
        }

        /// <summary>
        /// Tüm event handler'ları temizler.
        /// </summary>
        public void ClearSubscribers()
        {
            MessageReceived = null;
            OnMerged = null;
            Log.Debug("[ChatBus] All subscribers cleared");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // Event handler'ları temizle
            MessageReceived = null;
            OnMerged = null;

            // Queue'yu temizle
            while (_messageQueue.TryDequeue(out _)) { }

            // Dictionary'leri temizle
            _lastMessageTime.Clear();

            // Semaphore'u dispose et
            _processingLock.Dispose();

            Log.Debug("[ChatBus] Disposed");
        }
    }

    /// <summary>
    /// Chat mesajı.
    /// </summary>
    public sealed class ChatMessage
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public ChatPlatform Platform { get; init; }
        public string Username { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Message { get; init; } = "";
        public string? AvatarUrl { get; init; }
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;
        public bool IsSubscriber { get; init; }
        public bool IsModerator { get; init; }
        public bool IsOwner { get; init; }
        public bool IsVerified { get; init; }
        public ChatMessageType Type { get; init; } = ChatMessageType.Normal;
        public string? DonationAmount { get; init; }
        public string? DonationCurrency { get; init; }
        public string? BadgeUrl { get; init; }
        public Dictionary<string, string> Metadata { get; init; } = new();

        /// <summary>
        /// Maskelenmiş kullanıcı adı (gizlilik için).
        /// </summary>
        public string MaskedUsername
        {
            get
            {
                if (string.IsNullOrEmpty(Username) || Username.Length < 3)
                    return "***";

                return Username[0] + new string('*', Username.Length - 2) + Username[^1];
            }
        }

        public override string ToString()
        {
            return $"[{Platform}] {DisplayName}: {Message}";
        }
    }

    /// <summary>
    /// Chat platformları.
    /// </summary>
    public enum ChatPlatform
    {
        Unknown = 0,
        YouTube = 1,
        Twitch = 2,
        TikTok = 3,
        Instagram = 4,
        Facebook = 5,
        Twitter = 6,
        Discord = 7,
        Kick = 8
    }

    /// <summary>
    /// Mesaj türleri.
    /// </summary>
    public enum ChatMessageType
    {
        Normal = 0,
        Superchat = 1,
        Subscription = 2,
        Gift = 3,
        Raid = 4,
        System = 5,
        Highlight = 6
    }

    /// <summary>
    /// Chat mesajı event argümanları.
    /// </summary>
    public sealed class ChatMessageEventArgs : EventArgs
    {
        public ChatMessage Message { get; }
        public DateTime ReceivedAt { get; }

        public ChatMessageEventArgs(ChatMessage message)
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
            ReceivedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// ChatBus istatistikleri.
    /// </summary>
    public sealed class ChatBusStatistics
    {
        public long TotalMessagesReceived { get; init; }
        public long TotalMessagesDropped { get; init; }
        public int ActivePlatforms { get; init; }

        public double DropRate => TotalMessagesReceived > 0
            ? (double)TotalMessagesDropped / TotalMessagesReceived * 100
            : 0;
    }
}