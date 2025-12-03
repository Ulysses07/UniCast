using System;
using System.Windows;
using System.Windows.Interop;

namespace UniCast.App.DirectX
{
    /// <summary>
    /// Direct3D yüzeyini WPF'e bağlayan D3DImage wrapper.
    /// DÜZELTME: Unmanaged resource yönetimi iyileştirildi.
    /// </summary>
    public class D3DImageSource : D3DImage, IDisposable
    {
        private IntPtr _surface;
        private bool _disposed;
        private bool _isLocked;

        /// <summary>
        /// D3D surface'i ayarlar.
        /// </summary>
        public void SetBackBuffer(IntPtr surface)
        {
            if (_disposed) return;

            // DÜZELTME: Önceki surface'i temizle
            if (_surface != IntPtr.Zero && _surface != surface)
            {
                ClearBackBuffer();
            }

            _surface = surface;

            try
            {
                Lock();
                _isLocked = true;
                SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface);
            }
            finally
            {
                if (_isLocked)
                {
                    Unlock();
                    _isLocked = false;
                }
            }
        }

        /// <summary>
        /// Back buffer'ı temizler.
        /// </summary>
        public void ClearBackBuffer()
        {
            if (_disposed) return;

            try
            {
                Lock();
                _isLocked = true;
                SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
                _surface = IntPtr.Zero;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"D3D ClearBackBuffer Error: {ex.Message}");
            }
            finally
            {
                if (_isLocked)
                {
                    // DÜZELTME v26: Boş catch'e loglama eklendi
                    try { Unlock(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[D3DImageSource.ClearBackBuffer] Unlock hatası: {ex.Message}"); }
                    _isLocked = false;
                }
            }
        }

        /// <summary>
        /// Frame'i günceller (dirty rect ile).
        /// </summary>
        public void Invalidate()
        {
            if (_disposed || _surface == IntPtr.Zero) return;

            try
            {
                Lock();
                _isLocked = true;

                if (PixelWidth > 0 && PixelHeight > 0)
                {
                    AddDirtyRect(new Int32Rect(0, 0, PixelWidth, PixelHeight));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"D3D Invalidate Error: {ex.Message}");
            }
            finally
            {
                if (_isLocked)
                {
                    // DÜZELTME v26: Boş catch'e loglama eklendi
                    try { Unlock(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[D3DImageSource.Invalidate] Unlock hatası: {ex.Message}"); }
                    _isLocked = false;
                }
            }
        }

        /// <summary>
        /// Belirli bir bölgeyi günceller.
        /// </summary>
        public void InvalidateRect(Int32Rect rect)
        {
            if (_disposed || _surface == IntPtr.Zero) return;

            try
            {
                Lock();
                _isLocked = true;

                // Sınırları kontrol et
                var validRect = new Int32Rect(
                    Math.Max(0, rect.X),
                    Math.Max(0, rect.Y),
                    Math.Min(rect.Width, PixelWidth - rect.X),
                    Math.Min(rect.Height, PixelHeight - rect.Y)
                );

                if (validRect.Width > 0 && validRect.Height > 0)
                {
                    AddDirtyRect(validRect);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"D3D InvalidateRect Error: {ex.Message}");
            }
            finally
            {
                if (_isLocked)
                {
                    // DÜZELTME v26: Boş catch'e loglama eklendi
                    try { Unlock(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[D3DImageSource.InvalidateRect] Unlock hatası: {ex.Message}"); }
                    _isLocked = false;
                }
            }
        }

        ~D3DImageSource()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// DÜZELTME: Unmanaged kaynakları düzgün temizle.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                // Managed kaynakları temizle
                // WPF Dispatcher üzerinde çalışıyorsak back buffer'ı temizle
                if (System.Windows.Threading.Dispatcher.CurrentDispatcher.CheckAccess())
                {
                    try
                    {
                        ClearBackBuffer();
                    }
                    catch (Exception ex)
                    {
                        // DÜZELTME v26: Boş catch'e loglama eklendi
                        System.Diagnostics.Debug.WriteLine($"[D3DImageSource.Dispose] ClearBackBuffer hatası: {ex.Message}");
                    }
                }
            }

            // Unmanaged kaynakları temizle
            // DÜZELTME: Surface pointer'ı sıfırla
            _surface = IntPtr.Zero;
            _isLocked = false;
        }
    }
}