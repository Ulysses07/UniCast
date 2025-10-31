using System;
using System.Windows.Interop;
using System.Windows;
using System.Runtime.InteropServices;

namespace UniCast.App.DirectX
{
    public class D3DImageSource : D3DImage, IDisposable
    {
        private IntPtr _surface;

        public void SetBackBuffer(IntPtr surface)
        {
            _surface = surface;
            Lock();
            SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface);
            Unlock();
        }

        public void Invalidate()
        {
            if (_surface == IntPtr.Zero) return;
            Lock();
            AddDirtyRect(new Int32Rect(0, 0, PixelWidth, PixelHeight));
            Unlock();
        }

        public void Dispose()
        {
            _surface = IntPtr.Zero;
        }
    }
}
