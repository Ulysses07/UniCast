using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UniCast.Encoder.Hardware
{
    /// <summary>
    /// Hardware encoder detection and management interface.
    /// Supports NVENC (NVIDIA), QSV (Intel), AMF (AMD).
    /// </summary>
    public interface IHardwareEncoderService : IDisposable
    {
        /// <summary>
        /// Available hardware encoders
        /// </summary>
        IReadOnlyList<HardwareEncoder> AvailableEncoders { get; }

        /// <summary>
        /// Best encoder based on performance ranking
        /// </summary>
        HardwareEncoder? BestEncoder { get; }

        /// <summary>
        /// Indicates if hardware encoding is available
        /// </summary>
        bool IsHardwareEncodingAvailable { get; }

        /// <summary>
        /// Indicates if detection is complete
        /// </summary>
        bool IsDetectionComplete { get; }

        /// <summary>
        /// Detect available hardware encoders
        /// </summary>
        /// <param name="ffmpegPath">Optional FFmpeg path</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>List of available encoders</returns>
        Task<IReadOnlyList<HardwareEncoder>> DetectEncodersAsync(
            string? ffmpegPath = null,
            CancellationToken ct = default);

        /// <summary>
        /// Get FFmpeg parameters for specified encoder type
        /// </summary>
        /// <param name="type">Encoder type</param>
        /// <param name="preset">Quality preset</param>
        /// <param name="bitrate">Target bitrate in kbps</param>
        /// <param name="fps">Frames per second</param>
        /// <returns>Encoder parameters</returns>
        EncoderParameters GetEncoderParameters(
            HardwareEncoderType type,
            EncoderPreset preset = EncoderPreset.Balanced,
            int bitrate = 6000,
            int fps = 30);

        /// <summary>
        /// Benchmark an encoder
        /// </summary>
        /// <param name="encoder">Encoder to benchmark</param>
        /// <param name="durationSeconds">Test duration</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Benchmark results</returns>
        Task<EncoderBenchmarkResult> BenchmarkEncoderAsync(
            HardwareEncoder encoder,
            int durationSeconds = 5,
            CancellationToken ct = default);
    }
}