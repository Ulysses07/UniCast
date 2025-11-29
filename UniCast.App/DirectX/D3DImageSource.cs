using System;
using System.Windows.Interop;
using System.Windows;

namespace UniCast.App.DirectX
{
    /// <summary>
    /// Direct3D yüzeyini WPF'e bağlayan D3DImage wrapper.
    /// </summary>
    public class D3DImageSource : D3DImage, IDisposable
    {
        private IntPtr _surface;
        private bool _disposed;

        public void SetBackBuffer(IntPtr surface)
        {
            if (_disposed) return;

            _surface = surface;
            Lock();
            SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface);
            Unlock();
        }

        public void Invalidate()
        {
            if (_disposed || _surface == IntPtr.Zero) return;

            try
            {
                Lock();
                if (PixelWidth > 0 && PixelHeight > 0)
                    AddDirtyRect(new Int32Rect(0, 0, PixelWidth, PixelHeight));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"D3D Invalidate Error: {ex.Message}");
            }
            finally
            {
                try { Unlock(); } catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _surface = IntPtr.Zero;

            // DÜZELTME: GC.SuppressFinalize eklendi
            GC.SuppressFinalize(this);
        }
    }
}