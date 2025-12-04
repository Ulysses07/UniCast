using UniCast.Tests.Helpers;

namespace UniCast.Tests.Services;

/// <summary>
/// SettingsData model testleri
/// Not: SettingsStore static olduğu için doğrudan test etmek zor.
/// Bu testler SettingsData modeli üzerinde çalışır.
/// </summary>
public class SettingsDataTests
{
    #region Default Values

    [Fact]
    public void NewSettingsData_ShouldHaveDefaultValues()
    {
        // Arrange & Act
        var settings = new UniCast.App.Services.SettingsData();

        // Assert - Video defaults
        settings.VideoResolution.Should().Be("1920x1080");
        settings.VideoWidth.Should().Be(1920);
        settings.VideoHeight.Should().Be(1080);
        settings.VideoKbps.Should().Be(4500);
        settings.Fps.Should().Be(30);
        settings.VideoEncoder.Should().Be("libx264");
        settings.VideoPreset.Should().Be("veryfast");

        // Assert - Audio defaults
        settings.AudioKbps.Should().Be(128);
        settings.AudioEncoder.Should().Be("aac");
        settings.AudioSampleRate.Should().Be(44100);
        settings.AudioChannels.Should().Be(2);

        // Assert - Overlay defaults
        settings.OverlayEnabled.Should().BeTrue();
        settings.OverlayOpacity.Should().Be(0.9);
        settings.OverlayPosition.Should().Be("BottomRight");

        // Assert - General defaults
        settings.Theme.Should().Be("Dark");
        settings.Language.Should().Be("tr-TR");
        settings.ChatEnabled.Should().BeTrue();
    }

    #endregion

    #region Normalize Tests

    [Fact]
    public void Normalize_WithExtremeValues_ShouldClamp()
    {
        // Arrange
        var settings = new UniCast.App.Services.SettingsData
        {
            VideoKbps = 100000, // Çok yüksek
            AudioKbps = 10,      // Çok düşük
            Fps = 500,           // Çok yüksek
            OverlayOpacity = 5.0 // Çok yüksek
        };

        // Act
        settings.Normalize();

        // Assert
        settings.VideoKbps.Should().Be(50000); // max 50000
        settings.AudioKbps.Should().Be(64);    // min 64
        settings.Fps.Should().Be(120);         // max 120
        settings.OverlayOpacity.Should().Be(1.0); // max 1.0
    }

    [Fact]
    public void Normalize_WithNullArrays_ShouldCreateEmptyArrays()
    {
        // Arrange
        var settings = new UniCast.App.Services.SettingsData
        {
            ChatBlockedWords = null!,
            ChatBlockedUsers = null!
        };

        // Act
        settings.Normalize();

        // Assert
        settings.ChatBlockedWords.Should().NotBeNull();
        settings.ChatBlockedWords.Should().BeEmpty();
        settings.ChatBlockedUsers.Should().NotBeNull();
        settings.ChatBlockedUsers.Should().BeEmpty();
    }

    [Fact]
    public void Normalize_WithEmptyRecordingPath_ShouldSetDefault()
    {
        // Arrange
        var settings = new UniCast.App.Services.SettingsData
        {
            RecordingPath = ""
        };

        // Act
        settings.Normalize();

        // Assert
        settings.RecordingPath.Should().NotBeNullOrEmpty();
        settings.RecordingPath.Should().Contain("UniCast Recordings");
    }

    [Theory]
    [InlineData(100, 500)]     // Çok düşük -> min
    [InlineData(60000, 50000)] // Çok yüksek -> max
    [InlineData(6000, 6000)]   // Normal -> aynı
    public void Normalize_VideoKbps_ShouldClampCorrectly(int input, int expected)
    {
        // Arrange
        var settings = new UniCast.App.Services.SettingsData { VideoKbps = input };

        // Act
        settings.Normalize();

        // Assert
        settings.VideoKbps.Should().Be(expected);
    }

    [Theory]
    [InlineData(10, 15)]   // Çok düşük -> min
    [InlineData(200, 120)] // Çok yüksek -> max
    [InlineData(60, 60)]   // Normal -> aynı
    public void Normalize_Fps_ShouldClampCorrectly(int input, int expected)
    {
        // Arrange
        var settings = new UniCast.App.Services.SettingsData { Fps = input };

        // Act
        settings.Normalize();

        // Assert
        settings.Fps.Should().Be(expected);
    }

    #endregion

    #region Alias Properties

    [Fact]
    public void AliasProperties_ShouldMapCorrectly()
    {
        // Arrange
        var settings = new UniCast.App.Services.SettingsData();

        // Act - Width/Height aliases
        settings.Width = 1280;
        settings.Height = 720;

        // Assert
        settings.VideoWidth.Should().Be(1280);
        settings.VideoHeight.Should().Be(720);
        settings.Width.Should().Be(1280);
        settings.Height.Should().Be(720);
    }

    [Fact]
    public void CameraAliases_ShouldMapToSelectedCamera()
    {
        // Arrange
        var settings = new UniCast.App.Services.SettingsData();
        const string cameraName = "Test Camera";

        // Act
        settings.SelectedCamera = cameraName;

        // Assert
        settings.DefaultCamera.Should().Be(cameraName);
        settings.SelectedVideoDevice.Should().Be(cameraName);
        settings.VideoDevice.Should().Be(cameraName);
    }

    [Fact]
    public void EncoderAlias_ShouldMapToVideoEncoder()
    {
        // Arrange
        var settings = new UniCast.App.Services.SettingsData();

        // Act
        settings.Encoder = "h264_nvenc";

        // Assert
        settings.VideoEncoder.Should().Be("h264_nvenc");
        settings.Encoder.Should().Be("h264_nvenc");
    }

    [Fact]
    public void RecordingAliases_ShouldMapCorrectly()
    {
        // Arrange
        var settings = new UniCast.App.Services.SettingsData();

        // Act
        settings.EnableLocalRecord = true;
        settings.RecordFolder = @"C:\Videos";

        // Assert
        settings.RecordingEnabled.Should().BeTrue();
        settings.RecordingPath.Should().Be(@"C:\Videos");
    }

    #endregion

    #region Overlay Settings

    [Theory]
    [InlineData(0.05, 0.1)]  // Çok düşük -> min
    [InlineData(1.5, 1.0)]   // Çok yüksek -> max
    [InlineData(0.5, 0.5)]   // Normal -> aynı
    public void Normalize_OverlayOpacity_ShouldClampCorrectly(double input, double expected)
    {
        // Arrange
        var settings = new UniCast.App.Services.SettingsData { OverlayOpacity = input };

        // Act
        settings.Normalize();

        // Assert
        settings.OverlayOpacity.Should().Be(expected);
    }

    [Theory]
    [InlineData(100, 200)]    // Çok küçük -> min
    [InlineData(3000, 1920)]  // Çok büyük -> max
    [InlineData(800, 800)]    // Normal -> aynı
    public void Normalize_OverlayWidth_ShouldClampCorrectly(int input, int expected)
    {
        // Arrange
        var settings = new UniCast.App.Services.SettingsData { OverlayWidth = input };

        // Act
        settings.Normalize();

        // Assert
        settings.OverlayWidth.Should().Be(expected);
    }

    #endregion

    #region Chat Settings

    [Fact]
    public void ChatSettings_DefaultValues_ShouldBeCorrect()
    {
        // Arrange & Act
        var settings = new UniCast.App.Services.SettingsData();

        // Assert
        settings.ChatEnabled.Should().BeTrue();
        settings.ChatShowTimestamps.Should().BeTrue();
        settings.ChatShowPlatformBadges.Should().BeTrue();
        settings.ChatFilterProfanity.Should().BeFalse();
        settings.ChatBlockedWords.Should().BeEmpty();
        settings.ChatBlockedUsers.Should().BeEmpty();
    }

    [Fact]
    public void ChatBlockedWords_ShouldBeSettable()
    {
        // Arrange
        var settings = new UniCast.App.Services.SettingsData();
        var blockedWords = new[] { "spam", "badword" };

        // Act
        settings.ChatBlockedWords = blockedWords;

        // Assert
        settings.ChatBlockedWords.Should().BeEquivalentTo(blockedWords);
    }

    #endregion

    #region Platform Settings

    [Fact]
    public void YouTubeSettings_DefaultRtmpUrl_ShouldBeCorrect()
    {
        // Arrange & Act
        var settings = new UniCast.App.Services.SettingsData();

        // Assert
        settings.YouTubeRtmpUrl.Should().Be("rtmp://a.rtmp.youtube.com/live2");
    }

    [Fact]
    public void TwitchSettings_DefaultRtmpUrl_ShouldBeCorrect()
    {
        // Arrange & Act
        var settings = new UniCast.App.Services.SettingsData();

        // Assert
        settings.TwitchRtmpUrl.Should().Be("rtmp://live.twitch.tv/app");
    }

    #endregion

    #region Audio Settings

    [Theory]
    [InlineData(32, 64)]   // Çok düşük -> min
    [InlineData(500, 320)] // Çok yüksek -> max
    [InlineData(192, 192)] // Normal -> aynı
    public void Normalize_AudioKbps_ShouldClampCorrectly(int input, int expected)
    {
        // Arrange
        var settings = new UniCast.App.Services.SettingsData { AudioKbps = input };

        // Act
        settings.Normalize();

        // Assert
        settings.AudioKbps.Should().Be(expected);
    }

    [Fact]
    public void AudioSettings_DefaultValues_ShouldBeCorrect()
    {
        // Arrange & Act
        var settings = new UniCast.App.Services.SettingsData();

        // Assert
        settings.AudioKbps.Should().Be(128);
        settings.AudioEncoder.Should().Be("aac");
        settings.AudioSampleRate.Should().Be(44100);
        settings.AudioChannels.Should().Be(2);
        settings.AudioDelayMs.Should().Be(0);
    }

    #endregion

    #region Recording Settings

    [Theory]
    [InlineData(-10, 1)]   // Negatif -> min
    [InlineData(150, 100)] // Çok yüksek -> max
    [InlineData(80, 80)]   // Normal -> aynı
    public void Normalize_RecordingQuality_ShouldClampCorrectly(int input, int expected)
    {
        // Arrange
        var settings = new UniCast.App.Services.SettingsData { RecordingQuality = input };

        // Act
        settings.Normalize();

        // Assert
        settings.RecordingQuality.Should().Be(expected);
    }

    [Fact]
    public void RecordingSettings_DefaultValues_ShouldBeCorrect()
    {
        // Arrange & Act
        var settings = new UniCast.App.Services.SettingsData();

        // Assert
        settings.RecordingEnabled.Should().BeFalse();
        settings.RecordingFormat.Should().Be("mp4");
        settings.RecordingQuality.Should().Be(80);
    }

    #endregion
}

/// <summary>
/// Video resolution parsing testleri
/// </summary>
public class VideoResolutionTests
{
    [Theory]
    [InlineData("1920x1080", 1920, 1080)]
    [InlineData("1280x720", 1280, 720)]
    [InlineData("2560x1440", 2560, 1440)]
    [InlineData("3840x2160", 3840, 2160)]
    public void ParseResolution_ShouldExtractWidthAndHeight(string resolution, int expectedWidth, int expectedHeight)
    {
        // Arrange & Act
        var parts = resolution.Split('x');
        var width = int.Parse(parts[0]);
        var height = int.Parse(parts[1]);

        // Assert
        width.Should().Be(expectedWidth);
        height.Should().Be(expectedHeight);
    }

    [Fact]
    public void StandardResolutions_ShouldHaveCorrectAspectRatio()
    {
        // Arrange
        var resolutions = new Dictionary<string, double>
        {
            { "1920x1080", 16.0 / 9.0 },
            { "1280x720", 16.0 / 9.0 },
            { "2560x1440", 16.0 / 9.0 },
            { "3840x2160", 16.0 / 9.0 }
        };

        // Act & Assert
        foreach (var (resolution, expectedRatio) in resolutions)
        {
            var parts = resolution.Split('x');
            var width = double.Parse(parts[0]);
            var height = double.Parse(parts[1]);
            var ratio = width / height;

            ratio.Should().BeApproximately(expectedRatio, 0.01);
        }
    }
}
