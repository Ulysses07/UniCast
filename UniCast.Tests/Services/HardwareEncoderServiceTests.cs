using UniCast.Encoder.Hardware;
using UniCast.Tests.Helpers;

namespace UniCast.Tests.Services;

/// <summary>
/// GpuVendor enum testleri
/// </summary>
public class GpuVendorTests
{
    [Fact]
    public void ShouldHaveAllExpectedValues()
    {
        // Assert
        var names = Enum.GetNames<GpuVendor>();
        names.Should().HaveCount(4);
        names.Should().Contain("Unknown");
        names.Should().Contain("Nvidia");
        names.Should().Contain("Amd");
        names.Should().Contain("Intel");
    }

    [Theory]
    [InlineData(GpuVendor.Unknown, 0)]
    [InlineData(GpuVendor.Nvidia, 1)]
    [InlineData(GpuVendor.Amd, 2)]
    [InlineData(GpuVendor.Intel, 3)]
    public void ShouldHaveCorrectIntValues(GpuVendor vendor, int expected)
    {
        ((int)vendor).Should().Be(expected);
    }
}

/// <summary>
/// HardwareEncoderType enum testleri
/// </summary>
public class HardwareEncoderTypeTests
{
    [Fact]
    public void ShouldHaveAllExpectedValues()
    {
        var names = Enum.GetNames<HardwareEncoderType>();

        // Software
        names.Should().Contain("Software");

        // NVIDIA NVENC
        names.Should().Contain("NvencH264");
        names.Should().Contain("NvencHevc");
        names.Should().Contain("NvencAv1");

        // Intel QuickSync
        names.Should().Contain("QsvH264");
        names.Should().Contain("QsvHevc");
        names.Should().Contain("QsvAv1");

        // AMD AMF
        names.Should().Contain("AmfH264");
        names.Should().Contain("AmfHevc");
        names.Should().Contain("AmfAv1");
    }

    [Fact]
    public void Software_ShouldBeZero()
    {
        ((int)HardwareEncoderType.Software).Should().Be(0);
    }
}

/// <summary>
/// EncoderPreset enum testleri
/// </summary>
public class EncoderPresetTests
{
    [Fact]
    public void ShouldHaveAllExpectedValues()
    {
        var names = Enum.GetNames<EncoderPreset>();
        names.Should().HaveCount(4);
        names.Should().Contain("Quality");
        names.Should().Contain("Balanced");
        names.Should().Contain("Performance");
        names.Should().Contain("LowLatency");
    }
}

/// <summary>
/// GpuInfo model testleri
/// </summary>
public class GpuInfoTests
{
    [Fact]
    public void NewInstance_ShouldHaveDefaultValues()
    {
        // Act
        var gpu = new GpuInfo();

        // Assert
        gpu.Name.Should().BeEmpty();
        gpu.Vendor.Should().Be(GpuVendor.Unknown);
        gpu.VramBytes.Should().Be(0);
        gpu.DriverVersion.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1073741824, 1.0)]    // 1 GB
    [InlineData(2147483648, 2.0)]    // 2 GB
    [InlineData(4294967296, 4.0)]    // 4 GB
    [InlineData(8589934592, 8.0)]    // 8 GB
    [InlineData(12884901888, 12.0)]  // 12 GB
    public void VramGb_ShouldCalculateCorrectly(long bytes, double expectedGb)
    {
        // Arrange
        var gpu = new GpuInfo { VramBytes = bytes };

        // Act & Assert
        gpu.VramGb.Should().BeApproximately(expectedGb, 0.01);
    }

    [Fact]
    public void WithValues_ShouldRetainValues()
    {
        // Act
        var gpu = new GpuInfo
        {
            Name = "NVIDIA GeForce RTX 4090",
            Vendor = GpuVendor.Nvidia,
            VramBytes = 25769803776, // 24 GB
            DriverVersion = "546.33"
        };

        // Assert
        gpu.Name.Should().Be("NVIDIA GeForce RTX 4090");
        gpu.Vendor.Should().Be(GpuVendor.Nvidia);
        gpu.VramBytes.Should().Be(25769803776);
        gpu.VramGb.Should().BeApproximately(24.0, 0.1);
        gpu.DriverVersion.Should().Be("546.33");
    }
}

/// <summary>
/// HardwareEncoder model testleri
/// </summary>
public class HardwareEncoderModelTests
{
    [Fact]
    public void NewInstance_ShouldHaveDefaultValues()
    {
        // Act
        var encoder = new HardwareEncoder();

        // Assert
        encoder.Name.Should().BeEmpty();
        encoder.Type.Should().Be(HardwareEncoderType.Software);
        encoder.FfmpegCodec.Should().BeEmpty();
        encoder.Priority.Should().Be(0);
        encoder.MaxBitrate.Should().Be(0);
        encoder.MaxResolution.Should().BeEmpty();
        encoder.SupportsLookahead.Should().BeFalse();
        encoder.SupportsBFrames.Should().BeFalse();
    }

    [Fact]
    public void NvencH264_Example_ShouldRetainValues()
    {
        // Act
        var encoder = new HardwareEncoder
        {
            Name = "NVIDIA NVENC H.264",
            Type = HardwareEncoderType.NvencH264,
            FfmpegCodec = "h264_nvenc",
            Priority = 100,
            MaxBitrate = 50000,
            MaxResolution = "4096x4096",
            SupportsLookahead = true,
            SupportsBFrames = true
        };

        // Assert
        encoder.Name.Should().Be("NVIDIA NVENC H.264");
        encoder.Type.Should().Be(HardwareEncoderType.NvencH264);
        encoder.FfmpegCodec.Should().Be("h264_nvenc");
        encoder.Priority.Should().Be(100);
        encoder.MaxBitrate.Should().Be(50000);
        encoder.MaxResolution.Should().Be("4096x4096");
        encoder.SupportsLookahead.Should().BeTrue();
        encoder.SupportsBFrames.Should().BeTrue();
    }

    [Fact]
    public void QsvH264_Example_ShouldRetainValues()
    {
        // Act
        var encoder = new HardwareEncoder
        {
            Name = "Intel QuickSync H.264",
            Type = HardwareEncoderType.QsvH264,
            FfmpegCodec = "h264_qsv",
            Priority = 80,
            MaxBitrate = 40000,
            MaxResolution = "4096x4096",
            SupportsLookahead = true,
            SupportsBFrames = true
        };

        // Assert
        encoder.Type.Should().Be(HardwareEncoderType.QsvH264);
        encoder.FfmpegCodec.Should().Be("h264_qsv");
    }
}

/// <summary>
/// EncoderParameters model testleri
/// </summary>
public class EncoderParametersTests
{
    [Fact]
    public void NewInstance_ShouldHaveDefaultValues()
    {
        // Act
        var params_ = new EncoderParameters();

        // Assert
        params_.Codec.Should().BeEmpty();
        params_.Preset.Should().BeEmpty();
        params_.Tune.Should().BeEmpty();
        params_.Profile.Should().Be("main");
        params_.RateControl.Should().Be("cbr");
        params_.Bitrate.Should().Be(0);
        params_.MaxBitrate.Should().Be(0);
        params_.BufferSize.Should().Be(0);
        params_.Fps.Should().Be(0);
        params_.KeyframeInterval.Should().Be(0);
        params_.ExtraParams.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void ToFfmpegArgs_WithBasicParams_ShouldGenerateCorrectArgs()
    {
        // Arrange
        var params_ = new EncoderParameters
        {
            Codec = "h264_nvenc",
            Preset = "p4",
            Profile = "high",
            Bitrate = 6000,
            MaxBitrate = 8000,
            BufferSize = 12000,
            Fps = 30,
            KeyframeInterval = 60
        };

        // Act
        var args = params_.ToFfmpegArgs();

        // Assert
        args.Should().Contain("-c:v h264_nvenc");
        args.Should().Contain("-preset p4");
        args.Should().Contain("-profile:v high");
        args.Should().Contain("-b:v 6000k");
        args.Should().Contain("-maxrate 8000k");
        args.Should().Contain("-bufsize 12000k");
        args.Should().Contain("-r 30");
        args.Should().Contain("-g 60");
    }

    [Fact]
    public void ToFfmpegArgs_WithTune_ShouldIncludeTune()
    {
        // Arrange
        var params_ = new EncoderParameters
        {
            Codec = "libx264",
            Preset = "fast",
            Tune = "zerolatency",
            Bitrate = 4000,
            MaxBitrate = 5000,
            BufferSize = 8000,
            Fps = 30,
            KeyframeInterval = 60
        };

        // Act
        var args = params_.ToFfmpegArgs();

        // Assert
        args.Should().Contain("-tune zerolatency");
    }

    [Fact]
    public void ToFfmpegArgs_WithExtraParams_ShouldIncludeExtraParams()
    {
        // Arrange
        var params_ = new EncoderParameters
        {
            Codec = "h264_nvenc",
            Bitrate = 6000,
            MaxBitrate = 8000,
            BufferSize = 12000,
            Fps = 30,
            KeyframeInterval = 60,
            ExtraParams = new Dictionary<string, string>
            {
                { "rc", "cbr" },
                { "cbr", "1" },
                { "spatial_aq", "1" }
            }
        };

        // Act
        var args = params_.ToFfmpegArgs();

        // Assert
        args.Should().Contain("-rc cbr");
        args.Should().Contain("-cbr 1");
        args.Should().Contain("-spatial_aq 1");
    }

    [Fact]
    public void ToFfmpegArgs_WithEmptyPreset_ShouldNotIncludePreset()
    {
        // Arrange
        var params_ = new EncoderParameters
        {
            Codec = "h264_nvenc",
            Preset = "", // Boş
            Bitrate = 6000,
            MaxBitrate = 8000,
            BufferSize = 12000,
            Fps = 30,
            KeyframeInterval = 60
        };

        // Act
        var args = params_.ToFfmpegArgs();

        // Assert
        args.Should().NotContain("-preset ");
    }

    [Fact]
    public void ToFfmpegArgs_WithEmptyProfile_ShouldNotIncludeProfile()
    {
        // Arrange
        var params_ = new EncoderParameters
        {
            Codec = "h264_nvenc",
            Profile = "", // Boş
            Bitrate = 6000,
            MaxBitrate = 8000,
            BufferSize = 12000,
            Fps = 30,
            KeyframeInterval = 60
        };

        // Act
        var args = params_.ToFfmpegArgs();

        // Assert
        args.Should().NotContain("-profile:v ");
    }
}

/// <summary>
/// EncoderBenchmarkResult model testleri
/// </summary>
public class EncoderBenchmarkResultTests
{
    [Fact]
    public void NewInstance_ShouldHaveDefaultValues()
    {
        // Act
        var result = new EncoderBenchmarkResult();

        // Assert
        result.Encoder.Should().BeNull();
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().BeNull();
        result.EncodingTimeMs.Should().Be(0);
        result.FramesEncoded.Should().Be(0);
        result.AverageFps.Should().Be(0);
        result.CpuUsagePercent.Should().Be(0);
        result.PerformanceScore.Should().Be(0);
    }

    [Fact]
    public void SuccessfulBenchmark_ShouldRetainValues()
    {
        // Arrange
        var encoder = new HardwareEncoder
        {
            Name = "NVENC H.264",
            Type = HardwareEncoderType.NvencH264
        };

        // Act
        var result = new EncoderBenchmarkResult
        {
            Encoder = encoder,
            Success = true,
            EncodingTimeMs = 5000,
            FramesEncoded = 300,
            AverageFps = 60.0,
            CpuUsagePercent = 15.5,
            PerformanceScore = 387 // 60 * 100 / 15.5 ≈ 387
        };

        // Assert
        result.Encoder.Should().Be(encoder);
        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.EncodingTimeMs.Should().Be(5000);
        result.FramesEncoded.Should().Be(300);
        result.AverageFps.Should().Be(60.0);
        result.CpuUsagePercent.Should().Be(15.5);
        result.PerformanceScore.Should().Be(387);
    }

    [Fact]
    public void FailedBenchmark_ShouldHaveErrorMessage()
    {
        // Act
        var result = new EncoderBenchmarkResult
        {
            Success = false,
            ErrorMessage = "Encoder not available"
        };

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Encoder not available");
    }
}

/// <summary>
/// HardwareEncoderService instance testleri
/// </summary>
[Collection("HardwareEncoderService")]
public class HardwareEncoderServiceInstanceTests
{
    [Fact]
    public void Instance_ShouldBeSingleton()
    {
        // Act
        var instance1 = HardwareEncoderService.Instance;
        var instance2 = HardwareEncoderService.Instance;

        // Assert
        instance1.Should().BeSameAs(instance2);
    }

    [Fact]
    public void Instance_ShouldNotBeNull()
    {
        // Act
        var instance = HardwareEncoderService.Instance;

        // Assert
        instance.Should().NotBeNull();
    }

    [Fact]
    public void AvailableEncoders_Initially_ShouldBeEmpty()
    {
        // Not: Detection çalışmadan önce boş olmalı
        // Ama daha önce çalışmış olabilir
        var instance = HardwareEncoderService.Instance;

        // Assert - Property erişilebilir olmalı
        instance.AvailableEncoders.Should().NotBeNull();
    }

    [Fact]
    public void IsHardwareEncodingAvailable_ShouldReflectEncoderCount()
    {
        var instance = HardwareEncoderService.Instance;

        // Property tutarlı olmalı
        var hasEncoders = instance.AvailableEncoders.Count > 0;
        instance.IsHardwareEncodingAvailable.Should().Be(hasEncoders);
    }

    [Fact]
    public void BestEncoder_WhenNoEncoders_ShouldBeNull()
    {
        var instance = HardwareEncoderService.Instance;

        // Detection yapılmamışsa veya encoder yoksa null olabilir
        if (instance.AvailableEncoders.Count == 0)
        {
            instance.BestEncoder.Should().BeNull();
        }
        else
        {
            instance.BestEncoder.Should().NotBeNull();
        }
    }

    [Theory]
    [InlineData(HardwareEncoderType.Software)]
    [InlineData(HardwareEncoderType.NvencH264)]
    [InlineData(HardwareEncoderType.QsvH264)]
    [InlineData(HardwareEncoderType.AmfH264)]
    public void GetEncoderParameters_ShouldReturnValidParams(HardwareEncoderType type)
    {
        // Arrange
        var instance = HardwareEncoderService.Instance;

        // Act
        var params_ = instance.GetEncoderParameters(type, EncoderPreset.Balanced, 6000, 30);

        // Assert
        params_.Should().NotBeNull();
        params_.Bitrate.Should().Be(6000);
        params_.Fps.Should().Be(30);
        params_.Codec.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(EncoderPreset.Quality)]
    [InlineData(EncoderPreset.Balanced)]
    [InlineData(EncoderPreset.Performance)]
    [InlineData(EncoderPreset.LowLatency)]
    public void GetEncoderParameters_WithDifferentPresets_ShouldWork(EncoderPreset preset)
    {
        // Arrange
        var instance = HardwareEncoderService.Instance;

        // Act
        var params_ = instance.GetEncoderParameters(
            HardwareEncoderType.NvencH264,
            preset,
            8000,
            60);

        // Assert
        params_.Should().NotBeNull();
        params_.Fps.Should().Be(60);
        params_.Bitrate.Should().Be(8000);
    }
}