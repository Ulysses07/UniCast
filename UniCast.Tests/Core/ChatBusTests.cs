using UniCast.Core.Chat;
using UniCast.Tests.Helpers;
using MockFactory = UniCast.Tests.Helpers.MockFactory;

namespace UniCast.Tests.Core;

/// <summary>
/// ChatBus unit testleri.
/// Collection attribute ile sequential çalıştırılır çünkü ChatBus singleton.
/// </summary>
[Collection("ChatBus")]
public class ChatBusTests : TestBase
{
    #region Publish Tests

    [Fact]
    public void Publish_WithValidMessage_ShouldTriggerMessageReceivedEvent()
    {
        // Arrange
        var bus = ChatBus.Instance;
        bus.ClearSubscribers();
        bus.ResetStatistics();

        ChatMessage? receivedMessage = null;
        bus.MessageReceived += (sender, e) => receivedMessage = e.Message;

        var message = new ChatMessage
        {
            Platform = ChatPlatform.YouTube,
            Username = "testuser",
            DisplayName = "Test User",
            Message = "Hello World!"
        };

        // Act
        bus.Publish(message);

        // Assert
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Username.Should().Be("testuser");
        receivedMessage.Message.Should().Be("Hello World!");
        receivedMessage.Platform.Should().Be(ChatPlatform.YouTube);

        // Cleanup
        bus.ClearSubscribers();
    }

    [Fact]
    public void Publish_WithNullMessage_ShouldNotThrow()
    {
        // Arrange
        var bus = ChatBus.Instance;

        // Act
        var action = () => bus.Publish(null!);

        // Assert
        action.Should().NotThrow();
    }

    [Fact]
    public void Publish_WithValidMessage_ShouldTriggerOnMergedEvent()
    {
        // Arrange
        var bus = ChatBus.Instance;
        bus.ClearSubscribers();
        bus.ResetStatistics();

        ChatMessage? mergedMessage = null;
        bus.OnMerged += msg => mergedMessage = msg;

        var message = new ChatMessage
        {
            Platform = ChatPlatform.Twitch,
            Username = "twitchuser",
            Message = "Twitch message"
        };

        // Act
        bus.Publish(message);

        // Assert
        mergedMessage.Should().NotBeNull();
        mergedMessage!.Platform.Should().Be(ChatPlatform.Twitch);

        // Cleanup
        bus.ClearSubscribers();
    }

    #endregion

    #region Statistics Tests

    [Fact]
    public void GetStatistics_AfterPublish_ShouldReturnCorrectCount()
    {
        // Arrange
        var bus = ChatBus.Instance;
        bus.ClearSubscribers();
        bus.ResetStatistics();

        var message = new ChatMessage
        {
            Platform = ChatPlatform.TikTok,
            Username = "tiktoker",
            Message = "TikTok message"
        };

        // Act
        bus.Publish(message);
        var stats = bus.GetStatistics();

        // Assert
        stats.TotalMessagesReceived.Should().Be(1);
    }

    [Fact]
    public void ResetStatistics_ShouldClearCounts()
    {
        // Arrange
        var bus = ChatBus.Instance;
        bus.Publish(new ChatMessage { Platform = ChatPlatform.YouTube, Message = "test" });

        // Act
        bus.ResetStatistics();
        var stats = bus.GetStatistics();

        // Assert
        stats.TotalMessagesReceived.Should().Be(0);
        stats.TotalMessagesDropped.Should().Be(0);
    }

    #endregion

    #region Rate Limiting Tests

    [Fact]
    public void Publish_RapidMessages_ShouldRateLimitSamePlatform()
    {
        // Arrange
        var bus = ChatBus.Instance;
        bus.ClearSubscribers();
        bus.ResetStatistics();

        var receivedCount = 0;
        bus.MessageReceived += (sender, e) => Interlocked.Increment(ref receivedCount);

        // Act - 10 mesaj çok hızlı gönder (aynı platform)
        for (int i = 0; i < 10; i++)
        {
            bus.Publish(new ChatMessage
            {
                Platform = ChatPlatform.Facebook,
                Username = $"user{i}",
                Message = $"Message {i}"
            });
        }

        // Assert - Rate limiting nedeniyle hepsi gelmemeli
        var stats = bus.GetStatistics();
        stats.TotalMessagesDropped.Should().BeGreaterThan(0);

        // Cleanup
        bus.ClearSubscribers();
    }

    [Fact]
    public void Publish_DifferentPlatforms_ShouldNotRateLimit()
    {
        // Arrange
        var bus = ChatBus.Instance;
        bus.ClearSubscribers();
        bus.ResetStatistics();

        var receivedCount = 0;
        bus.MessageReceived += (sender, e) => Interlocked.Increment(ref receivedCount);

        // Act - Farklı platformlardan mesaj (rate limiting platform bazlı)
        bus.Publish(new ChatMessage { Platform = ChatPlatform.YouTube, Message = "YT" });
        bus.Publish(new ChatMessage { Platform = ChatPlatform.Twitch, Message = "Twitch" });
        bus.Publish(new ChatMessage { Platform = ChatPlatform.TikTok, Message = "TikTok" });

        // Assert - Sequential çalışır, 3 mesaj gelmeli
        receivedCount.Should().Be(3);

        // Cleanup
        bus.ClearSubscribers();
    }

    #endregion

    #region ClearSubscribers Tests

    [Fact]
    public void ClearSubscribers_ShouldRemoveAllEventHandlers()
    {
        // Arrange
        var bus = ChatBus.Instance;
        var receivedCount = 0;
        bus.MessageReceived += (sender, e) => receivedCount++;
        bus.OnMerged += msg => receivedCount++;

        // Act
        bus.ClearSubscribers();
        bus.Publish(new ChatMessage { Platform = ChatPlatform.YouTube, Message = "test" });

        // Assert
        receivedCount.Should().Be(0);
    }

    #endregion

    #region Async Tests

    [Fact]
    public async Task PublishAsync_WithValidMessage_ShouldPublishSuccessfully()
    {
        // Arrange
        var bus = ChatBus.Instance;
        bus.ClearSubscribers();
        bus.ResetStatistics();

        var tcs = new TaskCompletionSource<ChatMessage>();
        bus.MessageReceived += (sender, e) => tcs.TrySetResult(e.Message);

        var message = new ChatMessage
        {
            Platform = ChatPlatform.Instagram,
            Username = "instauser",
            Message = "Instagram post"
        };

        // Act
        await bus.PublishAsync(message, CancellationToken);

        // Assert
        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        received.Should().NotBeNull();
        received.Platform.Should().Be(ChatPlatform.Instagram);

        // Cleanup
        bus.ClearSubscribers();
    }

    [Fact]
    public async Task PublishAsync_WithCancelledToken_ShouldNotPublish()
    {
        // Arrange
        var bus = ChatBus.Instance;
        bus.ClearSubscribers();
        bus.ResetStatistics();

        var receivedCount = 0;
        bus.MessageReceived += (sender, e) => Interlocked.Increment(ref receivedCount);

        var cancelledToken = MockFactory.CreateCancelledToken();

        // Act
        await bus.PublishAsync(new ChatMessage { Message = "test" }, cancelledToken);

        // Assert
        receivedCount.Should().Be(0);

        // Cleanup
        bus.ClearSubscribers();
    }

    #endregion

    #region PublishBatch Tests

    [Fact]
    public void PublishBatch_WithMultipleMessages_ShouldPublishAll()
    {
        // Arrange
        var bus = ChatBus.Instance;
        bus.ClearSubscribers();
        bus.ResetStatistics();

        var receivedMessages = new List<ChatMessage>();
        bus.MessageReceived += (sender, e) => receivedMessages.Add(e.Message);

        var messages = new[]
        {
            new ChatMessage { Platform = ChatPlatform.YouTube, Message = "YT1" },
            new ChatMessage { Platform = ChatPlatform.Twitch, Message = "Twitch1" },
            new ChatMessage { Platform = ChatPlatform.Discord, Message = "Discord1" }
        };

        // Act
        bus.PublishBatch(messages);

        // Assert - Sequential çalışır, 3 mesaj gelmeli
        receivedMessages.Should().HaveCount(3);

        // Cleanup
        bus.ClearSubscribers();
    }

    #endregion
}

/// <summary>
/// ChatMessage unit testleri
/// </summary>
public class ChatMessageTests
{
    [Fact]
    public void ChatMessage_NewInstance_ShouldHaveDefaults()
    {
        // Arrange & Act
        var message = new ChatMessage();

        // Assert
        message.Id.Should().NotBeNullOrEmpty();
        message.Platform.Should().Be(ChatPlatform.Unknown);
        message.Username.Should().BeEmpty();
        message.Message.Should().BeEmpty();
        message.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        message.Type.Should().Be(ChatMessageType.Normal);
        message.Metadata.Should().NotBeNull();
    }

    [Theory]
    [InlineData("ab", "***")]
    [InlineData("abc", "a*c")]
    [InlineData("abcd", "a**d")]
    [InlineData("username", "u******e")]
    public void MaskedUsername_ShouldMaskCorrectly(string username, string expected)
    {
        // Arrange
        var message = new ChatMessage { Username = username };

        // Act
        var masked = message.MaskedUsername;

        // Assert
        masked.Should().Be(expected);
    }

    [Fact]
    public void ToString_ShouldReturnFormattedString()
    {
        // Arrange
        var message = new ChatMessage
        {
            Platform = ChatPlatform.Twitch,
            DisplayName = "TestStreamer",
            Message = "Hello everyone!"
        };

        // Act
        var result = message.ToString();

        // Assert
        result.Should().Be("[Twitch] TestStreamer: Hello everyone!");
    }
}

/// <summary>
/// ChatBusStatistics testleri
/// </summary>
public class ChatBusStatisticsTests
{
    [Fact]
    public void DropRate_WithNoMessages_ShouldBeZero()
    {
        // Arrange
        var stats = new ChatBusStatistics
        {
            TotalMessagesReceived = 0,
            TotalMessagesDropped = 0
        };

        // Act & Assert
        stats.DropRate.Should().Be(0);
    }

    [Fact]
    public void DropRate_WithDroppedMessages_ShouldCalculateCorrectly()
    {
        // Arrange
        var stats = new ChatBusStatistics
        {
            TotalMessagesReceived = 100,
            TotalMessagesDropped = 25
        };

        // Act & Assert
        stats.DropRate.Should().Be(25);
    }
}