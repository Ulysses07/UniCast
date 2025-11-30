using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.Core.Chat.Ingestors
{
    /// <summary>
    /// TikTok Live Chat ingestor.
    /// NOT: TikTok'un resmi API'si sınırlı, üçüncü parti kütüphaneler gerekebilir.
    /// </summary>
    public sealed class TikTokChatIngestor : BaseChatIngestor
    {
        public override ChatPlatform Platform => ChatPlatform.TikTok;

        public TikTokChatIngestor(string username) : base(username)
        {
        }

        protected override Task ConnectAsync(CancellationToken ct)
        {
            Log.Information("[TikTok] Bağlanılıyor: @{Username}", _identifier);
            // TODO: TikTok API implementasyonu
            return Task.CompletedTask;
        }

        protected override Task DisconnectAsync()
        {
            return Task.CompletedTask;
        }

        protected override async Task RunMessageLoopAsync(CancellationToken ct)
        {
            // Mock implementation
            var random = new Random();
            var messages = new[] { "🔥", "❤️", "Harikasın!", "Devam!", "👏" };

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(3000, ct);

                    if (random.Next(100) < 20) // %20 şans
                    {
                        var mockMessage = new ChatMessage
                        {
                            Platform = ChatPlatform.TikTok,
                            Username = $"tiktok_user_{random.Next(1000)}",
                            DisplayName = $"TikTokUser{random.Next(100)}",
                            Message = messages[random.Next(messages.Length)],
                            Timestamp = DateTime.UtcNow
                        };

                        PublishMessage(mockMessage);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Instagram Live Chat ingestor.
    /// NOT: Instagram'ın resmi live chat API'si yok, web scraping veya üçüncü parti gerekebilir.
    /// </summary>
    public sealed class InstagramChatIngestor : BaseChatIngestor
    {
        public override ChatPlatform Platform => ChatPlatform.Instagram;

        public InstagramChatIngestor(string username) : base(username)
        {
        }

        protected override Task ConnectAsync(CancellationToken ct)
        {
            Log.Information("[Instagram] Bağlanılıyor: @{Username}", _identifier);
            // TODO: Instagram API implementasyonu
            return Task.CompletedTask;
        }

        protected override Task DisconnectAsync()
        {
            return Task.CompletedTask;
        }

        protected override async Task RunMessageLoopAsync(CancellationToken ct)
        {
            // Mock implementation
            var random = new Random();
            var messages = new[] { "💕", "Çok güzel!", "😍", "Merhaba!", "🙌" };

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(4000, ct);

                    if (random.Next(100) < 15) // %15 şans
                    {
                        var mockMessage = new ChatMessage
                        {
                            Platform = ChatPlatform.Instagram,
                            Username = $"ig_user_{random.Next(1000)}",
                            DisplayName = $"InstaUser{random.Next(100)}",
                            Message = messages[random.Next(messages.Length)],
                            IsVerified = random.Next(100) < 5,
                            Timestamp = DateTime.UtcNow
                        };

                        PublishMessage(mockMessage);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Facebook Live Chat ingestor.
    /// Facebook Graph API kullanır.
    /// </summary>
    public sealed class FacebookChatIngestor : BaseChatIngestor
    {
        public override ChatPlatform Platform => ChatPlatform.Facebook;

        /// <summary>
        /// Facebook Access Token.
        /// </summary>
        public string? AccessToken { get; set; }

        public FacebookChatIngestor(string pageId) : base(pageId)
        {
        }

        protected override Task ConnectAsync(CancellationToken ct)
        {
            Log.Information("[Facebook] Bağlanılıyor: Page {PageId}", _identifier);

            if (string.IsNullOrEmpty(AccessToken))
            {
                Log.Warning("[Facebook] Access Token bulunamadı, mock modda çalışılacak");
            }

            // TODO: Facebook Graph API implementasyonu
            return Task.CompletedTask;
        }

        protected override Task DisconnectAsync()
        {
            return Task.CompletedTask;
        }

        protected override async Task RunMessageLoopAsync(CancellationToken ct)
        {
            // Mock implementation
            var random = new Random();
            var messages = new[] { "👍", "Süper!", "Paylaşıyorum", "Harika içerik!", "👀" };

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, ct);

                    if (random.Next(100) < 10) // %10 şans
                    {
                        var mockMessage = new ChatMessage
                        {
                            Platform = ChatPlatform.Facebook,
                            Username = $"fb_user_{random.Next(1000)}",
                            DisplayName = $"Facebook User {random.Next(100)}",
                            Message = messages[random.Next(messages.Length)],
                            Timestamp = DateTime.UtcNow
                        };

                        PublishMessage(mockMessage);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}