using System;

namespace UniCast.Overlay
{
    /// <summary>
    /// Overlay modülü devre dışı. Önceki IOverlayBus referansları kaldırıldı.
    /// Bu sınıf yalnızca derleme bütünlüğü içindir.
    /// </summary>
    public sealed class OverlayServer : IDisposable
    {
        public void Start()
        {
            // Boş: overlay artık devre dışı
        }

        public void Stop()
        {
            // Boş
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
