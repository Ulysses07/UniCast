using System;
using System.Threading.Tasks;

namespace UniCast.App.Services
{
    /// <summary>
    /// Audio monitoring service interface.
    /// Provides audio level monitoring and mute control.
    /// </summary>
    public interface IAudioService : IDisposable
    {
        /// <summary>
        /// Fired when audio level changes (0.0 - 1.0)
        /// </summary>
        event Action<float>? OnLevelChange;

        /// <summary>
        /// Fired when mute state changes
        /// </summary>
        event Action<bool>? OnMuteChange;

        /// <summary>
        /// Initialize audio monitoring for the specified device
        /// </summary>
        /// <param name="deviceId">Device ID or empty for default</param>
        Task InitializeAsync(string deviceId);

        /// <summary>
        /// Toggle mute state
        /// </summary>
        void ToggleMute();

        /// <summary>
        /// Stop audio monitoring
        /// </summary>
        void StopMonitoring();
    }
}