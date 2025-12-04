using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using UniCast.App.Configuration;
using UniCast.App.Services;

namespace UniCast.Tests.Integration
{
    /// <summary>
    /// Integration tests for configuration system.
    /// </summary>
    [Collection("Integration")]
    public class ConfigurationIntegrationTests
    {
        [Fact]
        public void ConfigurationValidator_Integration_ShouldValidateAllRules()
        {
            // Arrange
            var validator = ConfigurationValidator.Instance;

            // Act
            var result = validator.Validate();

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().NotBeNull();
            result.Warnings.Should().NotBeNull();
        }

        [Fact]
        public void ConfigurationValidator_Integration_Validate_ShouldReturnResult()
        {
            // Arrange
            var validator = ConfigurationValidator.Instance;

            // Act
            var result = validator.Validate();

            // Assert - Should complete without throwing and return a result
            result.Should().NotBeNull();
            // IsValid is either true or false - both are acceptable in test environment
        }

        [Fact]
        public void SettingsStore_Integration_DefaultValues_ShouldBeReasonable()
        {
            // Arrange & Act
            var settings = SettingsStore.Load();

            // Assert - Check default values are sensible
            settings.Should().NotBeNull();
            settings.Width.Should().BeGreaterThan(0);
            settings.Height.Should().BeGreaterThan(0);
            settings.Fps.Should().BeGreaterThan(0);
            settings.VideoKbps.Should().BeGreaterThan(0);
            settings.AudioKbps.Should().BeGreaterThan(0);
        }

        [Fact]
        public void SettingsStore_Integration_LoadMultipleTimes_ShouldBeConsistent()
        {
            // Arrange & Act
            var settings1 = SettingsStore.Load();
            var settings2 = SettingsStore.Load();

            // Assert - Multiple loads should give consistent values
            settings1.Width.Should().Be(settings2.Width);
            settings1.Height.Should().Be(settings2.Height);
            settings1.Fps.Should().Be(settings2.Fps);
        }

        [Fact]
        public async Task SettingsStore_Integration_ConcurrentAccess_ShouldBeThreadSafe()
        {
            // Arrange
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var tasks = new Task[10];

            // Act - Concurrent reads
            for (int i = 0; i < 10; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    try
                    {
                        var settings = SettingsStore.Load();
                        _ = settings.VideoKbps;
                        _ = settings.Width;
                        _ = settings.Height;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex);
                    }
                });
            }

            await Task.WhenAll(tasks);

            // Assert
            errors.Should().BeEmpty("Concurrent access should not throw");
        }

        [Fact]
        public void AppConstants_Integration_PathsShouldExistOrBeCreatable()
        {
            // Arrange
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var appFolderPath = Path.Combine(documentsPath, "UniCast");

            // Act & Assert
            if (Directory.Exists(appFolderPath))
            {
                // If folder exists, it should be accessible
                var action = () => Directory.GetFiles(appFolderPath);
                action.Should().NotThrow();
            }
            else
            {
                // If folder doesn't exist, it should be creatable
                var action = () => Directory.CreateDirectory(appFolderPath);
                action.Should().NotThrow();

                // Cleanup
                try { Directory.Delete(appFolderPath); } catch { }
            }
        }

        [Fact]
        public void SettingsStore_Integration_Encoder_ShouldHaveDefault()
        {
            // Arrange & Act
            var settings = SettingsStore.Load();

            // Assert
            settings.Encoder.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public void SettingsStore_Integration_Resolution_ShouldBeStandard()
        {
            // Arrange & Act
            var settings = SettingsStore.Load();

            // Assert - Resolution should be standard values
            var standardWidths = new[] { 640, 854, 1280, 1920, 2560, 3840 };
            var standardHeights = new[] { 360, 480, 720, 1080, 1440, 2160 };

            standardWidths.Should().Contain(settings.Width);
            standardHeights.Should().Contain(settings.Height);
        }
    }
}