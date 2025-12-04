using System;
using System.Threading.Tasks;
using UniCast.Licensing.Models;

namespace UniCast.Licensing
{
    /// <summary>
    /// License management service interface.
    /// Handles license activation, validation, and status tracking.
    /// </summary>
    public interface ILicenseManager : IDisposable
    {
        /// <summary>
        /// License server URL
        /// </summary>
        string LicenseServerUrl { get; set; }

        /// <summary>
        /// Online validation interval in hours
        /// </summary>
        int OnlineValidationIntervalHours { get; set; }

        /// <summary>
        /// Allow offline mode when server is unreachable
        /// </summary>
        bool AllowOfflineMode { get; set; }

        /// <summary>
        /// Maximum retry attempts for network operations
        /// </summary>
        int MaxRetryAttempts { get; set; }

        /// <summary>
        /// Delay between retry attempts in milliseconds
        /// </summary>
        int RetryDelayMs { get; set; }

        /// <summary>
        /// Fired when license status changes
        /// </summary>
        event EventHandler<LicenseStatusChangedEventArgs>? StatusChanged;

        /// <summary>
        /// Fired when validation completes
        /// </summary>
        event EventHandler<LicenseValidationResult>? ValidationCompleted;

        /// <summary>
        /// Initialize the license system and validate current license.
        /// Should be called at application startup.
        /// </summary>
        Task<LicenseValidationResult> InitializeAsync();

        /// <summary>
        /// Activate a license with the given key
        /// </summary>
        /// <param name="licenseKey">License key to activate</param>
        Task<LicenseValidationResult> ActivateAsync(string licenseKey);

        /// <summary>
        /// Synchronous license activation (legacy)
        /// </summary>
        /// <param name="licenseKey">License key to activate</param>
        ActivationResult ActivateLicense(string licenseKey);

        /// <summary>
        /// Deactivate the current license
        /// </summary>
        Task<bool> DeactivateAsync();

        /// <summary>
        /// Validate the current license
        /// </summary>
        Task<LicenseValidationResult> ValidateAsync();

        /// <summary>
        /// Check if license is currently valid
        /// </summary>
        bool IsLicenseValid();

        /// <summary>
        /// Check if support/maintenance is active
        /// </summary>
        bool IsSupportActive();

        /// <summary>
        /// Get current license information
        /// </summary>
        LicenseInfo GetLicenseInfo();

        /// <summary>
        /// Start a trial license
        /// </summary>
        LicenseValidationResult StartTrial();
    }
}