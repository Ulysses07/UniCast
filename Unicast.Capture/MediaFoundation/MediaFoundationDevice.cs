namespace UniCast.Capture.MediaFoundation
{
    public sealed class MediaFoundationDevice
    {
        public required string FriendlyName { get; init; }   // UI için
        public required string SymbolicLink { get; init; }   // MF/OS ID
        public string? DevicePath { get; init; }             // Bazı sürücüler verir
        public string? Manufacturer { get; init; }           // Opsiyonel meta
    }
}