using UniCast.Licensing.Models;
using UniCast.Tests.Helpers;

namespace UniCast.Tests.Licensing;

/// <summary>
/// LicenseData model testleri
/// </summary>
public class LicenseDataTests
{
    #region Default Values

    [Fact]
    public void NewLicenseData_ShouldHaveDefaultValues()
    {
        // Arrange & Act
        var license = new LicenseData();

        // Assert
        license.LicenseId.Should().BeEmpty();
        license.LicenseKey.Should().BeEmpty();
        license.Type.Should().Be(LicenseType.Trial);
        license.MaxMachines.Should().Be(1);
        license.OfflineGraceDays.Should().Be(7);
        license.Activations.Should().NotBeNull().And.BeEmpty();
        license.Metadata.Should().NotBeNull().And.BeEmpty();
    }

    #endregion

    #region IsTrial / IsLifetime

    [Theory]
    [InlineData(LicenseType.Trial, true, false)]
    [InlineData(LicenseType.Lifetime, false, true)]
    public void LicenseTypeProperties_ShouldReturnCorrectValues(
        LicenseType type, bool expectedIsTrial, bool expectedIsLifetime)
    {
        // Arrange
        var license = new LicenseData { Type = type };

        // Assert
        license.IsTrial.Should().Be(expectedIsTrial);
        license.IsLifetime.Should().Be(expectedIsLifetime);
    }

    #endregion

    #region IsExpired

    [Fact]
    public void IsExpired_WithFutureDate_ShouldReturnFalse()
    {
        // Arrange
        var license = new LicenseData
        {
            Type = LicenseType.Trial,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(14)
        };

        // Assert
        license.IsExpired.Should().BeFalse();
    }

    [Fact]
    public void IsExpired_WithPastDate_ShouldReturnTrue()
    {
        // Arrange
        var license = new LicenseData
        {
            Type = LicenseType.Trial,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(-1)
        };

        // Assert
        license.IsExpired.Should().BeTrue();
    }

    [Fact]
    public void IsExpired_LifetimeLicense_WithMaxValue_ShouldReturnFalse()
    {
        // Arrange
        var license = new LicenseData
        {
            Type = LicenseType.Lifetime,
            ExpiresAtUtc = DateTime.MaxValue
        };

        // Assert
        license.IsExpired.Should().BeFalse();
    }

    #endregion

    #region DaysRemaining

    [Fact]
    public void DaysRemaining_TrialWith7Days_ShouldReturn7()
    {
        // Arrange
        var license = new LicenseData
        {
            Type = LicenseType.Trial,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
        };

        // Assert
        license.DaysRemaining.Should().BeInRange(6, 8); // Tolerans
    }

    [Fact]
    public void DaysRemaining_ExpiredTrial_ShouldReturnZero()
    {
        // Arrange
        var license = new LicenseData
        {
            Type = LicenseType.Trial,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(-5)
        };

        // Assert
        license.DaysRemaining.Should().Be(0);
    }

    [Fact]
    public void DaysRemaining_LifetimeLicense_ShouldReturnMaxValue()
    {
        // Arrange
        var license = new LicenseData
        {
            Type = LicenseType.Lifetime,
            ExpiresAtUtc = DateTime.MaxValue
        };

        // Assert
        license.DaysRemaining.Should().Be(int.MaxValue);
    }

    #endregion

    #region Support Expiry

    [Fact]
    public void IsSupportActive_WithFutureDate_ShouldReturnTrue()
    {
        // Arrange
        var license = new LicenseData
        {
            SupportExpiryUtc = DateTime.UtcNow.AddDays(365)
        };

        // Assert
        license.IsSupportActive.Should().BeTrue();
    }

    [Fact]
    public void IsSupportActive_WithPastDate_ShouldReturnFalse()
    {
        // Arrange
        var license = new LicenseData
        {
            SupportExpiryUtc = DateTime.UtcNow.AddDays(-30)
        };

        // Assert
        license.IsSupportActive.Should().BeFalse();
    }

    [Fact]
    public void SupportDaysRemaining_With30Days_ShouldReturn30()
    {
        // Arrange
        var license = new LicenseData
        {
            SupportExpiryUtc = DateTime.UtcNow.AddDays(30)
        };

        // Assert
        license.SupportDaysRemaining.Should().BeInRange(29, 31);
    }

    #endregion

    #region GetSignableContent

    [Fact]
    public void GetSignableContent_ShouldReturnConsistentFormat()
    {
        // Arrange
        var issuedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var expiresAt = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var supportExpiry = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var license = new LicenseData
        {
            LicenseId = "test-license-id",
            LicenseKey = "XXXXX-XXXXX-XXXXX",
            Type = LicenseType.Lifetime,
            LicenseeName = "Test User",
            LicenseeEmail = "test@example.com",
            IssuedAtUtc = issuedAt,
            ExpiresAtUtc = expiresAt,
            SupportExpiryUtc = supportExpiry,
            MaxMachines = 2
        };

        // Act
        var content1 = license.GetSignableContent();
        var content2 = license.GetSignableContent();

        // Assert
        content1.Should().Be(content2); // Deterministic
        content1.Should().Contain("test-license-id");
        content1.Should().Contain("XXXXX-XXXXX-XXXXX");
        content1.Should().Contain("Test User");
        content1.Should().Contain("test@example.com");
    }

    [Fact]
    public void GetSignableContent_WithActivations_ShouldIncludeHardwareIds()
    {
        // Arrange
        var license = new LicenseData
        {
            LicenseId = "lid",
            LicenseKey = "lkey",
            Type = LicenseType.Trial,
            LicenseeName = "Name",
            LicenseeEmail = "email@test.com",
            IssuedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(14),
            SupportExpiryUtc = DateTime.UtcNow.AddDays(365),
            MaxMachines = 1,
            Activations = new List<HardwareActivation>
            {
                new() { HardwareId = "HW-123-ABC" },
                new() { HardwareId = "HW-456-DEF" }
            }
        };

        // Act
        var content = license.GetSignableContent();

        // Assert
        content.Should().Contain("HW-123-ABC");
        content.Should().Contain("HW-456-DEF");
    }

    #endregion

    #region ToString

    [Fact]
    public void ToString_TrialLicense_ShouldShowDaysRemaining()
    {
        // Arrange
        var license = new LicenseData
        {
            LicenseId = "12345678-abcd-efgh-ijkl-mnopqrstuvwx",
            Type = LicenseType.Trial,
            LicenseeName = "Test User",
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            SupportExpiryUtc = DateTime.UtcNow.AddDays(365)
        };

        // Act
        var result = license.ToString();

        // Assert
        result.Should().Contain("Trial");
        result.Should().Contain("Test User");
        result.Should().Contain("12345678");
    }

    [Fact]
    public void ToString_LifetimeLicense_ShouldShowLifetime()
    {
        // Arrange
        var license = new LicenseData
        {
            LicenseId = "abcdefgh-1234-5678-9012-abcdefghijkl",
            Type = LicenseType.Lifetime,
            LicenseeName = "Pro User",
            ExpiresAtUtc = DateTime.MaxValue,
            SupportExpiryUtc = DateTime.UtcNow.AddDays(365)
        };

        // Act
        var result = license.ToString();

        // Assert
        result.Should().Contain("Ömür Boyu");
        result.Should().Contain("Pro User");
    }

    #endregion
}

/// <summary>
/// LicenseType enum testleri
/// </summary>
public class LicenseTypeTests
{
    [Fact]
    public void LicenseType_ShouldHaveExpectedValues()
    {
        // Assert
        ((int)LicenseType.Trial).Should().Be(0);
        ((int)LicenseType.Lifetime).Should().Be(1);
    }
}

/// <summary>
/// HardwareActivation testleri
/// </summary>
public class HardwareActivationTests
{
    [Fact]
    public void NewHardwareActivation_ShouldHaveDefaults()
    {
        // Arrange & Act
        var activation = new HardwareActivation();

        // Assert
        activation.HardwareId.Should().BeEmpty();
        activation.MachineName.Should().BeEmpty();
        activation.ActivatedAtUtc.Should().Be(default(DateTime));
    }

    [Fact]
    public void HardwareActivation_WithValues_ShouldRetainValues()
    {
        // Arrange
        var activatedAt = DateTime.UtcNow;

        var activation = new HardwareActivation
        {
            HardwareId = "HW-TEST-123",
            MachineName = "DESKTOP-TEST",
            ActivatedAtUtc = activatedAt,
            LastSeenUtc = activatedAt
        };

        // Assert
        activation.HardwareId.Should().Be("HW-TEST-123");
        activation.MachineName.Should().Be("DESKTOP-TEST");
        activation.ActivatedAtUtc.Should().Be(activatedAt);
        activation.LastSeenUtc.Should().Be(activatedAt);
    }
}

/// <summary>
/// LicenseValidationResult testleri
/// </summary>
public class LicenseValidationResultTests
{
    [Fact]
    public void Success_ShouldCreateValidResult()
    {
        // Arrange
        var license = new LicenseData
        {
            Type = LicenseType.Lifetime,
            LicenseeName = "Test"
        };

        // Act
        var result = LicenseValidationResult.Success(license);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Status.Should().Be(LicenseStatus.Valid);
        result.License.Should().Be(license);
        result.Message.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Failure_ShouldCreateInvalidResult()
    {
        // Act
        var result = LicenseValidationResult.Failure(
            LicenseStatus.Expired,
            "License expired",
            "Additional details");

        // Assert
        result.IsValid.Should().BeFalse();
        result.Status.Should().Be(LicenseStatus.Expired);
        result.Message.Should().Be("License expired");
        result.Details.Should().Be("Additional details");
        result.License.Should().BeNull();
    }
}

/// <summary>
/// LicenseStatus enum testleri
/// </summary>
public class LicenseStatusTests
{
    [Fact]
    public void LicenseStatus_ShouldHaveAllExpectedValues()
    {
        // Assert - Gerçek enum değerlerini kontrol et
        var names = Enum.GetNames<LicenseStatus>();
        names.Should().Contain("Valid");
        names.Should().Contain("NotFound");
        names.Should().Contain("Expired");
        names.Should().Contain("InvalidSignature");
        names.Should().Contain("Revoked");
        names.Should().Contain("Tampered");
        names.Should().Contain("HardwareMismatch");
        names.Should().Contain("GracePeriod");
    }
}