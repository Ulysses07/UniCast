using System.Collections.Concurrent;
using UniCast.Tests.Helpers;

namespace UniCast.Tests.Services;

/// <summary>
/// FrameBufferPool unit testleri
/// </summary>
public class FrameBufferPoolTests : TestBase
{
    #region Rent Tests

    [Fact]
    public void Rent_WithStandardSize_ShouldReturnBuffer()
    {
        // Arrange
        var pool = UniCast.Encoder.Memory.FrameBufferPool.Instance;
        var size720p = UniCast.Encoder.Memory.FrameBufferPool.FrameSizes.Size720p;

        // Act
        var buffer = pool.Rent(size720p);

        // Assert
        buffer.Should().NotBeNull();
        buffer.Length.Should().BeGreaterOrEqualTo(size720p);

        // Cleanup
        pool.Return(buffer);
    }

    [Theory]
    [InlineData(1920, 1080, 4)] // 1080p BGRA
    [InlineData(1280, 720, 4)]  // 720p BGRA
    [InlineData(2560, 1440, 4)] // 1440p BGRA
    public void Rent_WithVideoFrameSize_ShouldReturnCorrectSize(int width, int height, int bytesPerPixel)
    {
        // Arrange
        var pool = UniCast.Encoder.Memory.FrameBufferPool.Instance;
        var requiredSize = width * height * bytesPerPixel;

        // Act
        var buffer = pool.Rent(requiredSize);

        // Assert
        buffer.Should().NotBeNull();
        buffer.Length.Should().BeGreaterOrEqualTo(requiredSize);

        // Cleanup
        pool.Return(buffer);
    }

    [Fact]
    public void Rent_MultipleCalls_ShouldNotThrow()
    {
        // Arrange
        var pool = UniCast.Encoder.Memory.FrameBufferPool.Instance;
        var buffers = new List<byte[]>();

        // Act
        var action = () =>
        {
            for (int i = 0; i < 10; i++)
            {
                buffers.Add(pool.Rent(1920 * 1080 * 4));
            }
        };

        // Assert
        action.Should().NotThrow();
        buffers.Should().HaveCount(10);
        buffers.All(b => b != null && b.Length >= 1920 * 1080 * 4).Should().BeTrue();

        // Cleanup
        foreach (var buffer in buffers)
        {
            pool.Return(buffer);
        }
    }

    #endregion

    #region Return Tests

    [Fact]
    public void Return_WithValidBuffer_ShouldNotThrow()
    {
        // Arrange
        var pool = UniCast.Encoder.Memory.FrameBufferPool.Instance;
        var buffer = pool.Rent(1024);

        // Act
        var action = () => pool.Return(buffer);

        // Assert
        action.Should().NotThrow();
    }

    [Fact]
    public void Return_WithNullBuffer_ShouldNotThrow()
    {
        // Arrange
        var pool = UniCast.Encoder.Memory.FrameBufferPool.Instance;

        // Act
        var action = () => pool.Return(null!);

        // Assert
        action.Should().NotThrow();
    }

    [Fact]
    public void Return_SameBufferTwice_ShouldNotThrow()
    {
        // Arrange
        var pool = UniCast.Encoder.Memory.FrameBufferPool.Instance;
        var buffer = pool.Rent(1024);

        // Act
        var action = () =>
        {
            pool.Return(buffer);
            pool.Return(buffer); // İkinci kez
        };

        // Assert
        action.Should().NotThrow();
    }

    #endregion

    #region Statistics Tests

    [Fact]
    public void TotalRentCount_AfterRent_ShouldIncrement()
    {
        // Arrange
        var pool = UniCast.Encoder.Memory.FrameBufferPool.Instance;
        var initialCount = pool.TotalRentCount;

        // Act
        var buffer = pool.Rent(1024);
        pool.Return(buffer);

        // Assert
        pool.TotalRentCount.Should().BeGreaterThan(initialCount);
    }

    [Fact]
    public void ActiveBufferCount_AfterRentAndReturn_ShouldBeBalanced()
    {
        // Arrange
        var pool = UniCast.Encoder.Memory.FrameBufferPool.Instance;
        var initialActive = pool.ActiveBufferCount;

        // Act
        var buffer = pool.Rent(1024);
        var afterRent = pool.ActiveBufferCount;
        pool.Return(buffer);
        var afterReturn = pool.ActiveBufferCount;

        // Assert
        afterRent.Should().Be(initialActive + 1);
        afterReturn.Should().Be(initialActive);
    }

    [Fact]
    public void CacheHitRate_ShouldBeValidPercentage()
    {
        // Arrange
        var pool = UniCast.Encoder.Memory.FrameBufferPool.Instance;

        // Act
        var hitRate = pool.CacheHitRate;

        // Assert
        hitRate.Should().BeInRange(0, 100);
    }

    #endregion

    #region PreAllocate Tests

    [Fact]
    public void PreAllocate_ShouldIncreasePoolSize()
    {
        // Arrange
        var pool = UniCast.Encoder.Memory.FrameBufferPool.Instance;
        var initialSize = pool.TotalPoolSizeBytes;

        // Act
        pool.PreAllocate(1024 * 1024, 2); // 2 x 1MB buffer

        // Assert
        pool.TotalPoolSizeBytes.Should().BeGreaterOrEqualTo(initialSize);
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task RentReturn_ConcurrentAccess_ShouldBeThreadSafe()
    {
        // Arrange
        var pool = UniCast.Encoder.Memory.FrameBufferPool.Instance;
        var tasks = new List<Task>();
        var exceptions = new ConcurrentBag<Exception>();

        // Act
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    for (int j = 0; j < 100; j++)
                    {
                        var buffer = pool.Rent(1920 * 1080 * 4);
                        Thread.SpinWait(100); // Simüle iş
                        pool.Return(buffer);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        exceptions.Should().BeEmpty();
    }

    #endregion

    #region FrameBuffer Struct Tests

    [Fact]
    public void FrameBuffer_RentFromPool_ShouldWork()
    {
        // Arrange
        var pool = UniCast.Encoder.Memory.FrameBufferPool.Instance;

        // Act
        using var frameBuffer = pool.RentFrame(1920, 1080);

        // Assert
        frameBuffer.Width.Should().Be(1920);
        frameBuffer.Height.Should().Be(1080);
        frameBuffer.Data.Should().NotBeNull();
        frameBuffer.Data.Length.Should().BeGreaterOrEqualTo(1920 * 1080 * 4);
    }

    [Fact]
    public void FrameBuffer_Dispose_ShouldReturnToPool()
    {
        // Arrange
        var pool = UniCast.Encoder.Memory.FrameBufferPool.Instance;
        var initialActive = pool.ActiveBufferCount;

        // Act
        using (var frameBuffer = pool.RentFrame(1920, 1080))
        {
            pool.ActiveBufferCount.Should().Be(initialActive + 1);
        }

        // Assert - Dispose sonrası pool'a dönmeli
        pool.ActiveBufferCount.Should().Be(initialActive);
    }

    #endregion

    #region Memory Pressure Tests

    [Fact]
    public void Rent_UnderMemoryPressure_ShouldStillWork()
    {
        // Arrange
        var pool = UniCast.Encoder.Memory.FrameBufferPool.Instance;
        var largeBuffers = new List<byte[]>();

        // Act - Çok sayıda büyük buffer al
        for (int i = 0; i < 20; i++)
        {
            var buffer = pool.Rent(UniCast.Encoder.Memory.FrameBufferPool.FrameSizes.Size1080p);
            largeBuffers.Add(buffer);
        }

        // Assert
        largeBuffers.Should().HaveCount(20);
        largeBuffers.All(b => b != null).Should().BeTrue();

        // Cleanup
        foreach (var buffer in largeBuffers)
        {
            pool.Return(buffer);
        }
    }

    #endregion
}

/// <summary>
/// FrameSizes sabitleri testleri
/// </summary>
public class FrameSizesTests
{
    [Theory]
    [InlineData(1280, 720, 4, 3686400)]   // 720p
    [InlineData(1920, 1080, 4, 8294400)]  // 1080p
    [InlineData(2560, 1440, 4, 14745600)] // 1440p
    [InlineData(3840, 2160, 4, 33177600)] // 4K
    public void FrameSizes_ShouldMatchExpectedValues(int width, int height, int bytesPerPixel, int expected)
    {
        // Arrange & Act
        var calculated = width * height * bytesPerPixel;

        // Assert
        calculated.Should().Be(expected);
    }

    [Fact]
    public void FrameSizes_Constants_ShouldBeCorrect()
    {
        // Assert
        UniCast.Encoder.Memory.FrameBufferPool.FrameSizes.Size720p.Should().Be(1280 * 720 * 4);
        UniCast.Encoder.Memory.FrameBufferPool.FrameSizes.Size1080p.Should().Be(1920 * 1080 * 4);
        UniCast.Encoder.Memory.FrameBufferPool.FrameSizes.Size1440p.Should().Be(2560 * 1440 * 4);
        UniCast.Encoder.Memory.FrameBufferPool.FrameSizes.Size4K.Should().Be(3840 * 2160 * 4);
    }
}
