namespace UniCast.Core.Models
{
    public enum StreamErrorCode
    {
        None,           // Başarılı
        FfmpegNotFound, // ffmpeg.exe bulunamadı
        CameraBusy,     // Kamera başka uygulama tarafından kullanılıyor
        InvalidConfig,  // Hedef URL yok veya ayarlar bozuk
        ProcessFailed,  // FFmpeg başladı ama hemen kapandı (ExitCode != 0)
        Unknown         // Beklenmedik hata
    }

    public sealed record StreamStartResult
    {
        public bool Success { get; init; }
        public StreamErrorCode ErrorCode { get; init; }
        public string? UserMessage { get; init; } // UI'da gösterilecek dostane mesaj
        public string? TechnicalLog { get; init; } // Log dosyasına yazılacak ham hata

        public static StreamStartResult Ok() => new() { Success = true, ErrorCode = StreamErrorCode.None };

        public static StreamStartResult Fail(StreamErrorCode code, string msg, string? techLog = null)
            => new() { Success = false, ErrorCode = code, UserMessage = msg, TechnicalLog = techLog };
    }
}