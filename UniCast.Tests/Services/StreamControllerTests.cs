using System.Reflection;
using UniCast.Core.Services;
using UniCast.Tests.Helpers;

namespace UniCast.Tests.Services;

/// <summary>
/// StreamConfiguration unit testleri
/// </summary>
public class StreamConfigurationTests
{
    [Fact]
    public void NewInstance_ShouldHaveDefaultValues()
    {
        // Act
        var config = new StreamConfiguration();

        // Assert
        config.StreamId.Should().NotBeNullOrEmpty();
        config.StreamId.Should().HaveLength(32); // Guid without hyphens
        config.InputSource.Should().BeEmpty();
        config.OutputUrl.Should().BeEmpty();
        config.VideoBitrate.Should().Be(2500);
        config.AudioBitrate.Should().Be(128);
        config.Fps.Should().Be(30);
        config.Preset.Should().Be("veryfast");
    }

    [Fact]
    public void StreamId_ShouldBeUniqueForEachInstance()
    {
        // Act
        var config1 = new StreamConfiguration();
        var config2 = new StreamConfiguration();

        // Assert
        config1.StreamId.Should().NotBe(config2.StreamId);
    }

    [Fact]
    public void WithCustomValues_ShouldRetainValues()
    {
        // Arrange & Act
        var config = new StreamConfiguration
        {
            StreamId = "custom-id",
            InputSource = "test.mp4",
            OutputUrl = "rtmp://server/live/key",
            VideoBitrate = 5000,
            AudioBitrate = 256,
            Fps = 60,
            Preset = "slow"
        };

        // Assert
        config.StreamId.Should().Be("custom-id");
        config.InputSource.Should().Be("test.mp4");
        config.OutputUrl.Should().Be("rtmp://server/live/key");
        config.VideoBitrate.Should().Be(5000);
        config.AudioBitrate.Should().Be(256);
        config.Fps.Should().Be(60);
        config.Preset.Should().Be("slow");
    }
}

/// <summary>
/// StreamInfo unit testleri
/// </summary>
public class StreamInfoTests
{
    [Fact]
    public void NewInstance_ShouldHaveDefaultValues()
    {
        // Act
        var info = new StreamInfo();

        // Assert
        info.Id.Should().BeEmpty();
        info.OutputUrl.Should().BeEmpty();
        info.StartedAt.Should().Be(default(DateTime));
        info.ProcessId.Should().Be(0);
    }

    [Fact]
    public void Uptime_ShouldCalculateCorrectly()
    {
        // Arrange
        var startTime = DateTime.UtcNow.AddMinutes(-5);
        var info = new StreamInfo
        {
            StartedAt = startTime
        };

        // Act
        var uptime = info.Uptime;

        // Assert
        uptime.TotalMinutes.Should().BeApproximately(5, 0.1);
    }

    [Fact]
    public void Uptime_WithRecentStart_ShouldBeSmall()
    {
        // Arrange
        var info = new StreamInfo
        {
            StartedAt = DateTime.UtcNow
        };

        // Act
        var uptime = info.Uptime;

        // Assert
        uptime.TotalSeconds.Should().BeLessThan(1);
    }

    [Fact]
    public void WithValues_ShouldRetainValues()
    {
        // Arrange
        var startTime = DateTime.UtcNow;

        // Act
        var info = new StreamInfo
        {
            Id = "stream-123",
            OutputUrl = "rtmp://test/live",
            StartedAt = startTime,
            ProcessId = 12345
        };

        // Assert
        info.Id.Should().Be("stream-123");
        info.OutputUrl.Should().Be("rtmp://test/live");
        info.StartedAt.Should().Be(startTime);
        info.ProcessId.Should().Be(12345);
    }
}

/// <summary>
/// StreamStatistics unit testleri
/// </summary>
public class StreamStatisticsTests
{
    [Fact]
    public void NewInstance_ShouldHaveDefaultValues()
    {
        // Act
        var stats = new StreamStatistics();

        // Assert
        stats.FrameCount.Should().Be(0);
        stats.CurrentFps.Should().Be(0);
        stats.CurrentBitrate.Should().Be(0);
        stats.TotalBytes.Should().Be(0);
        stats.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void WithValues_ShouldRetainValues()
    {
        // Arrange
        var timestamp = DateTime.UtcNow;

        // Act
        var stats = new StreamStatistics
        {
            FrameCount = 9000,
            CurrentFps = 30.5,
            CurrentBitrate = 4500.75,
            TotalBytes = 1024 * 1024 * 100, // 100 MB
            Timestamp = timestamp
        };

        // Assert
        stats.FrameCount.Should().Be(9000);
        stats.CurrentFps.Should().Be(30.5);
        stats.CurrentBitrate.Should().Be(4500.75);
        stats.TotalBytes.Should().Be(104857600);
        stats.Timestamp.Should().Be(timestamp);
    }
}

/// <summary>
/// StreamState enum testleri
/// </summary>
public class StreamStateTests
{
    [Fact]
    public void ShouldHaveAllExpectedValues()
    {
        // Assert
        var names = Enum.GetNames<StreamState>();
        names.Should().Contain("Stopped");
        names.Should().Contain("Starting");
        names.Should().Contain("Running");
        names.Should().Contain("Stopping");
        names.Should().Contain("Error");
    }

    [Theory]
    [InlineData(StreamState.Stopped, 0)]
    [InlineData(StreamState.Starting, 1)]
    [InlineData(StreamState.Running, 2)]
    [InlineData(StreamState.Stopping, 3)]
    [InlineData(StreamState.Error, 4)]
    public void ShouldHaveCorrectIntValues(StreamState state, int expected)
    {
        // Assert
        ((int)state).Should().Be(expected);
    }
}

/// <summary>
/// StreamStateChangedEventArgs testleri
/// </summary>
public class StreamStateChangedEventArgsTests
{
    [Theory]
    [InlineData(StreamState.Running)]
    [InlineData(StreamState.Stopped)]
    [InlineData(StreamState.Error)]
    public void Constructor_ShouldSetNewState(StreamState state)
    {
        // Act
        var args = new StreamStateChangedEventArgs(state);

        // Assert
        args.NewState.Should().Be(state);
        args.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }
}

/// <summary>
/// StreamStatisticsEventArgs testleri
/// </summary>
public class StreamStatisticsEventArgsTests
{
    [Fact]
    public void Constructor_ShouldSetStatistics()
    {
        // Arrange
        var stats = new StreamStatistics
        {
            FrameCount = 1000,
            CurrentFps = 30
        };

        // Act
        var args = new StreamStatisticsEventArgs(stats);

        // Assert
        args.Statistics.Should().Be(stats);
        args.Statistics.FrameCount.Should().Be(1000);
        args.Statistics.CurrentFps.Should().Be(30);
    }
}

/// <summary>
/// StreamController URL maskeleme testleri (güvenlik)
/// </summary>
public class StreamControllerSecurityTests
{
    // MaskSensitiveUrl private method - reflection ile test
    private static string InvokeMaskSensitiveUrl(string url)
    {
        var method = typeof(StreamController).GetMethod(
            "MaskSensitiveUrl",
            BindingFlags.NonPublic | BindingFlags.Static);

        return (string)method!.Invoke(null, new object[] { url })!;
    }

    // MaskSensitiveArgs private method - reflection ile test
    private static string InvokeMaskSensitiveArgs(string args)
    {
        var method = typeof(StreamController).GetMethod(
            "MaskSensitiveArgs",
            BindingFlags.NonPublic | BindingFlags.Static);

        return (string)method!.Invoke(null, new object[] { args })!;
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void MaskSensitiveUrl_WithEmptyOrNull_ShouldReturnSame(string? input, string? expected)
    {
        // Act
        var result = InvokeMaskSensitiveUrl(input!);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void MaskSensitiveUrl_WithYouTubeRtmp_ShouldMaskStreamKey()
    {
        // Arrange
        var url = "rtmp://a.rtmp.youtube.com/live2/abcd-efgh-ijkl-mnop";

        // Act
        var masked = InvokeMaskSensitiveUrl(url);

        // Assert
        masked.Should().StartWith("rtmp://a.rtmp.youtube.com/live2/abcd");
        masked.Should().Contain("*");
        masked.Should().NotContain("efgh");
        masked.Should().NotContain("ijkl");
        masked.Should().NotContain("mnop");
    }

    [Fact]
    public void MaskSensitiveUrl_WithTwitchRtmp_ShouldMaskStreamKey()
    {
        // Arrange
        var url = "rtmp://live.twitch.tv/app/live_123456789_abcdefghijklmnop";

        // Act
        var masked = InvokeMaskSensitiveUrl(url);

        // Assert
        masked.Should().StartWith("rtmp://live.twitch.tv/app/live");
        masked.Should().Contain("*");
        // Stream key'in büyük kısmı görünmemeli
        masked.Should().NotContain("abcdefghijklmnop");
    }

    [Fact]
    public void MaskSensitiveArgs_WithRtmpUrl_ShouldMaskStreamKey()
    {
        // Arrange
        var args = "-f flv \"rtmp://a.rtmp.youtube.com/live2/secret-stream-key-here\"";

        // Act
        var masked = InvokeMaskSensitiveArgs(args);

        // Assert
        masked.Should().Contain("rtmp://a.rtmp.youtube.com/live2/secr");
        masked.Should().Contain("*");
        masked.Should().NotContain("secret-stream-key-here");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void MaskSensitiveArgs_WithEmptyOrNull_ShouldReturnSame(string? input)
    {
        // Act
        var result = InvokeMaskSensitiveArgs(input!);

        // Assert
        result.Should().Be(input);
    }

    [Fact]
    public void MaskSensitiveArgs_WithNoRtmpUrl_ShouldReturnSame()
    {
        // Arrange
        var args = "-i test.mp4 -c:v libx264 output.mp4";

        // Act
        var masked = InvokeMaskSensitiveArgs(args);

        // Assert
        masked.Should().Be(args);
    }
}

/// <summary>
/// StreamController instance testleri
/// </summary>
[Collection("StreamController")]
public class StreamControllerInstanceTests
{
    [Fact]
    public void Instance_ShouldBeSingleton()
    {
        // Act
        var instance1 = StreamController.Instance;
        var instance2 = StreamController.Instance;

        // Assert
        instance1.Should().BeSameAs(instance2);
    }

    [Fact]
    public void Instance_ShouldNotBeNull()
    {
        // Act
        var instance = StreamController.Instance;

        // Assert
        instance.Should().NotBeNull();
    }

    [Fact]
    public void ProcessTimeout_Default_ShouldBe30Seconds()
    {
        // Act
        var controller = StreamController.Instance;

        // Assert
        controller.ProcessTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void IsRunning_ShouldBeAccessibleProperty()
    {
        // Act
        var controller = StreamController.Instance;

        // Assert - Property erişilebilir olmalı ve exception fırlatmamalı
        Action act = () => { var _ = controller.IsRunning; };
        act.Should().NotThrow();
    }
}