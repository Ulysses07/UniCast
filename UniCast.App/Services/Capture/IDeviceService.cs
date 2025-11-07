namespace UniCast.App.Services.Capture
{
    public interface IDeviceService
    {
        Task<IReadOnlyList<string>> GetVideoFriendlyNamesAsync();
        Task<IReadOnlyList<string>> GetAudioFriendlyNamesAsync();

        /// dshow tabanlı FFmpeg giriş argümanı:
        /// -f dshow -i video="NAME":audio="NAME"
        string BuildFfmpegInputArgs(string? videoFriendlyName, string? audioFriendlyName,
                                    int? width = null, int? height = null, int? fps = null);
    }
}
