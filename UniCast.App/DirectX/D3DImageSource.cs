using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using UniCast.App;

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

        // DÜZELTME: Finalizer eklendi
        ~D3DImageSource()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            // Managed kaynakları temizle (disposing == true ise)
            if (disposing)
            {
                // Managed kaynaklar varsa burada temizlenir
            }

            // Unmanaged kaynakları temizle
            _surface = IntPtr.Zero;
        }
    }
}