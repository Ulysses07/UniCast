using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace UniCast.App.Services
{
    /// <summary>
    /// Camera preview service interface.
    /// Provides camera capture and frame display.
    /// </summary>
    public interface IPreviewService : IDisposable
    {
        /// <summary>
        /// Fired when a new frame is captured
        /// </summary>
        event Action<ImageSource>? OnFrame;

        /// <summary>
        /// Indicates if preview is currently running
        /// </summary>
        bool IsRunning { get; }
        
        /// <summary>
        /// Indicates if streaming pipe is active
        /// </summary>
        bool IsStreaming { get; }

        /// <summary>
        /// Start camera preview
        /// </summary>
        /// <param name="cameraIndex">Camera index (0 for default)</param>
        /// <param name="width">Frame width</param>
        /// <param name="height">Frame height</param>
        /// <param name="fps">Frames per second</param>
        /// <param name="rotation">Camera rotation in degrees (0, 90, 180, 270)</param>
        Task StartAsync(int cameraIndex, int width, int height, int fps, int rotation = 0);

        /// <summary>
        /// Stop camera preview
        /// </summary>
        Task StopAsync();
        
        /// <summary>
        /// Start streaming frames to FFmpeg via named pipe
        /// </summary>
        Task StartStreamingAsync(CancellationToken ct = default);
        
        /// <summary>
        /// Stop streaming frames to FFmpeg
        /// </summary>
        void StopStreaming();
        
        /// <summary>
        /// Get the named pipe name for FFmpeg input
        /// </summary>
        string? GetPipeName();
    }
}