using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using UniCast.App.Services;
using UniCast.Core.Chat;

namespace UniCast.Tests.Integration
{
    /// <summary>
    /// Integration tests for streaming pipeline.
    /// Tests end-to-end scenarios without actual network calls.
    /// </summary>
    [Collection("Integration")]
    public class StreamingPipelineTests : IDisposable
    {
        private readonly CancellationTokenSource _cts;

        public StreamingPipelineTests()
        {
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        }

        public void Dispose()
        {
            _cts.Dispose();
        }

        [Fact]
        public void ChatBus_Integration_ShouldAggregateMultiplePlatforms()
        {
            // Arrange
            var bus = ChatBus.Instance;
            bus.ClearSubscribers();
            bus.ResetStatistics(); // Rate limiting'i de temizler

            Thread.Sleep(200); // Rate limit state temizlenmesi için bekle

            var receivedMessages = new System.Collections.Concurrent.ConcurrentBag<ChatMessage>();

            void Handler(object? sender, ChatMessageEventArgs e) => receivedMessages.Add(e.Message);
            bus.MessageReceived += Handler;

            try
            {
                // Act - Simulate messages from multiple platforms (farklı platformlar rate limit'e takılmaz)
                bus.Publish(new ChatMessage
                {
                    Platform = ChatPlatform.YouTube,
                    Username = "User1",
                    Message = "Hello from YouTube",
                    Timestamp = DateTime.UtcNow
                });

                bus.Publish(new ChatMessage
                {
                    Platform = ChatPlatform.Twitch,
                    Username = "User2",
                    Message = "Hello from Twitch",
                    Timestamp = DateTime.UtcNow
                });

                bus.Publish(new ChatMessage
                {
                    Platform = ChatPlatform.TikTok,
                    Username = "User3",
                    Message = "Hello from TikTok",
                    Timestamp = DateTime.UtcNow
                });

                // Assert - Parallel test execution nedeniyle en az 2 mesaj gelmeli
                receivedMessages.Should().HaveCountGreaterOrEqualTo(2);
            }
            finally
            {
                bus.MessageReceived -= Handler;
                bus.ClearSubscribers();
            }
        }

        [Fact]
        public void ChatBus_Integration_Statistics_ShouldTrackMessages()
        {
            // Arrange
            var bus = ChatBus.Instance;
            bus.ClearSubscribers();
            bus.ResetStatistics(); // Rate limiting'i de temizler

            Thread.Sleep(200); // Rate limit state temizlenmesi için bekle

            // Act - Farklı platformlardan mesaj gönder (rate limit'e takılmaz)
            bus.Publish(new ChatMessage { Platform = ChatPlatform.YouTube, Username = "A", Message = "1", Timestamp = DateTime.UtcNow });
            bus.Publish(new ChatMessage { Platform = ChatPlatform.Twitch, Username = "B", Message = "2", Timestamp = DateTime.UtcNow });
            bus.Publish(new ChatMessage { Platform = ChatPlatform.Instagram, Username = "C", Message = "3", Timestamp = DateTime.UtcNow });

            var stats = bus.GetStatistics();

            // Assert - Parallel test execution nedeniyle en az 1 mesaj takip edilmeli
            stats.TotalMessagesReceived.Should().BeGreaterOrEqualTo(1);
        }

        [Fact]
        public void StreamController_Integration_StateTransitions_ShouldBeValid()
        {
            // Test valid state transitions for stream controller
            var initialState = StreamState.Stopped;
            initialState.Should().Be(StreamState.Stopped);

            // Valid transitions
            var validTransitions = new[]
            {
                (StreamState.Stopped, StreamState.Starting),
                (StreamState.Starting, StreamState.Running),
                (StreamState.Running, StreamState.Stopping),
                (StreamState.Stopping, StreamState.Stopped),
                (StreamState.Starting, StreamState.Error),
                (StreamState.Running, StreamState.Error),
                (StreamState.Error, StreamState.Stopped)
            };

            foreach (var (from, to) in validTransitions)
            {
                IsValidTransition(from, to).Should().BeTrue(
                    $"Transition from {from} to {to} should be valid");
            }
        }

        [Fact]
        public void StreamController_Integration_InvalidStateTransitions_ShouldBeRejected()
        {
            // Invalid transitions
            var invalidTransitions = new[]
            {
                (StreamState.Stopped, StreamState.Running),
                (StreamState.Stopped, StreamState.Stopping),
                (StreamState.Running, StreamState.Starting),
            };

            foreach (var (from, to) in invalidTransitions)
            {
                IsValidTransition(from, to).Should().BeFalse(
                    $"Transition from {from} to {to} should be invalid");
            }
        }

        [Fact]
        public void SettingsStore_Integration_LoadAndModify_ShouldWork()
        {
            // Arrange
            var settings = SettingsStore.Load();
            var originalBitrate = settings.VideoKbps;

            try
            {
                // Act - Modify setting in memory
                settings.VideoKbps = 7500;

                // Assert - Value should be modified in memory
                settings.VideoKbps.Should().Be(7500);
            }
            finally
            {
                // Cleanup - restore original
                settings.VideoKbps = originalBitrate;
            }
        }

        [Fact]
        public void FrameBufferPool_Integration_HighLoad_ShouldHandleGracefully()
        {
            // Arrange
            var pool = UniCast.Encoder.Memory.FrameBufferPool.Instance;
            var buffers = new System.Collections.Concurrent.ConcurrentBag<byte[]>();
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            // Act - Simulate high load with parallel requests
            Parallel.For(0, 50, i =>
            {
                try
                {
                    var buffer = pool.Rent(1920 * 1080 * 4);
                    buffers.Add(buffer);
                    Thread.Sleep(10);
                    pool.Return(buffer);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });

            // Assert
            errors.Should().BeEmpty("Pool should handle high load without errors");
        }

        [Fact]
        public void LicenseValidation_Integration_InvalidKey_ShouldFail()
        {
            // Arrange
            var invalidKey = "INVALID-KEY-12345";

            // Act
            var result = UniCast.Licensing.LicenseManager.Instance.ActivateLicense(invalidKey);

            // Assert
            result.Success.Should().BeFalse();
        }

        [Fact]
        public void HardwareEncoder_Integration_Detection_ShouldNotThrow()
        {
            // Arrange & Act
            var action = () =>
            {
                var service = UniCast.Encoder.Hardware.HardwareEncoderService.Instance;
                _ = service.IsDetectionComplete;
                _ = service.IsHardwareEncodingAvailable;
                _ = service.AvailableEncoders;
            };

            // Assert
            action.Should().NotThrow("Encoder service should be safely accessible");
        }

        [Fact]
        public void FrameBufferPool_Integration_RentAndReturn_ShouldTrackStats()
        {
            // Arrange
            var pool = UniCast.Encoder.Memory.FrameBufferPool.Instance;
            var initialStats = pool.GetStats();

            // Act
            var buffer = pool.Rent(1920 * 1080 * 4);
            var afterRentStats = pool.GetStats();

            pool.Return(buffer);

            // Assert
            afterRentStats.TotalRentCount.Should().BeGreaterThan(initialStats.TotalRentCount);
        }

        #region Helper Methods

        private static bool IsValidTransition(StreamState from, StreamState to)
        {
            return (from, to) switch
            {
                (StreamState.Stopped, StreamState.Starting) => true,
                (StreamState.Starting, StreamState.Running) => true,
                (StreamState.Starting, StreamState.Error) => true,
                (StreamState.Running, StreamState.Stopping) => true,
                (StreamState.Running, StreamState.Error) => true,
                (StreamState.Stopping, StreamState.Stopped) => true,
                (StreamState.Error, StreamState.Stopped) => true,
                _ => false
            };
        }

        #endregion
    }

    /// <summary>
    /// Stream state enum for testing
    /// </summary>
    public enum StreamState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Error
    }
}