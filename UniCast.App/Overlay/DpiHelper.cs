using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Serilog;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace UniCast.App.Overlay
{
    /// <summary>
    /// DÜZELTME v18: DPI Awareness helper
    /// Farklı DPI ayarlı monitörlerde overlay'in doğru görünmesini sağlar
    /// </summary>
    public static class DpiHelper
    {
        #region Native Methods

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        private static extern int GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int MDT_EFFECTIVE_DPI = 0;

        // DPI Awareness Context değerleri
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_UNAWARE = new(-1);
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = new(-2);
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = new(-3);
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

        #endregion

        #region Properties

        /// <summary>
        /// Sistem varsayılan DPI (genellikle 96)
        /// </summary>
        public const double DefaultDpi = 96.0;

        /// <summary>
        /// Mevcut DPI ölçeği
        /// </summary>
        public static double CurrentScale { get; private set; } = 1.0;

        /// <summary>
        /// Mevcut DPI değeri
        /// </summary>
        public static double CurrentDpi { get; private set; } = DefaultDpi;

        #endregion

        #region Initialization

        /// <summary>
        /// DPI awareness'ı başlat (App.xaml.cs OnStartup'ta çağrılmalı)
        /// </summary>
        public static bool InitializeDpiAwareness()
        {
            try
            {
                // Windows 10 1703+ için Per-Monitor V2
                if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                {
                    Log.Information("[DPI] Per-Monitor Aware V2 aktif");
                    return true;
                }

                // Fallback: Per-Monitor V1
                if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE))
                {
                    Log.Information("[DPI] Per-Monitor Aware aktif");
                    return true;
                }

                Log.Warning("[DPI] DPI awareness ayarlanamadı");
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DPI] DPI awareness başlatma hatası");
                return false;
            }
        }

        #endregion

        #region DPI Calculation

        /// <summary>
        /// Pencere için DPI değerini al
        /// </summary>
        public static double GetDpiForWindow(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    return GetSystemDpi();
                }

                // Windows 10+
                var dpi = GetDpiForWindow(hwnd);
                if (dpi > 0)
                {
                    return dpi;
                }

                // Fallback: Monitor DPI
                return GetDpiForMonitor(hwnd);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DPI] Pencere DPI alınamadı");
                return DefaultDpi;
            }
        }

        /// <summary>
        /// Monitor DPI değerini al
        /// </summary>
        public static double GetDpiForMonitor(IntPtr hwnd)
        {
            try
            {
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor == IntPtr.Zero)
                {
                    return GetSystemDpi();
                }

                var result = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY);
                if (result == 0) // S_OK
                {
                    return dpiX;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DPI] Monitor DPI alınamadı");
            }

            return GetSystemDpi();
        }

        /// <summary>
        /// Sistem DPI değerini al
        /// </summary>
        public static double GetSystemDpi()
        {
            try
            {
                using var source = new HwndSource(new HwndSourceParameters());
                var matrix = source.CompositionTarget?.TransformToDevice;

                if (matrix.HasValue)
                {
                    return DefaultDpi * matrix.Value.M11;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DPI] Sistem DPI alınamadı");
            }

            return DefaultDpi;
        }

        /// <summary>
        /// DPI ölçeğini hesapla (1.0 = 100%, 1.25 = 125%, vb.)
        /// </summary>
        public static double GetDpiScale(double dpi)
        {
            return dpi / DefaultDpi;
        }

        /// <summary>
        /// Pencere için DPI ölçeğini al
        /// </summary>
        public static double GetDpiScaleForWindow(Window window)
        {
            var dpi = GetDpiForWindow(window);
            return GetDpiScale(dpi);
        }

        #endregion

        #region Coordinate Conversion

        /// <summary>
        /// WPF birimini ekran piksellerine çevir
        /// </summary>
        public static double WpfToScreen(double wpfValue, double dpiScale)
        {
            return wpfValue * dpiScale;
        }

        /// <summary>
        /// Ekran pikselini WPF birimine çevir
        /// </summary>
        public static double ScreenToWpf(double screenValue, double dpiScale)
        {
            return screenValue / dpiScale;
        }

        /// <summary>
        /// Point'i WPF'den ekran koordinatlarına çevir
        /// </summary>
        public static Point WpfToScreen(Point wpfPoint, double dpiScale)
        {
            return new Point(
                wpfPoint.X * dpiScale,
                wpfPoint.Y * dpiScale);
        }

        /// <summary>
        /// Point'i ekran koordinatlarından WPF'e çevir
        /// </summary>
        public static Point ScreenToWpf(Point screenPoint, double dpiScale)
        {
            return new Point(
                screenPoint.X / dpiScale,
                screenPoint.Y / dpiScale);
        }

        /// <summary>
        /// Size'ı WPF'den ekran boyutlarına çevir
        /// </summary>
        public static Size WpfToScreen(Size wpfSize, double dpiScale)
        {
            return new Size(
                wpfSize.Width * dpiScale,
                wpfSize.Height * dpiScale);
        }

        /// <summary>
        /// Size'ı ekran boyutlarından WPF'e çevir
        /// </summary>
        public static Size ScreenToWpf(Size screenSize, double dpiScale)
        {
            return new Size(
                screenSize.Width / dpiScale,
                screenSize.Height / dpiScale);
        }

        /// <summary>
        /// Rect'i WPF'den ekran koordinatlarına çevir
        /// </summary>
        public static Rect WpfToScreen(Rect wpfRect, double dpiScale)
        {
            return new Rect(
                WpfToScreen(wpfRect.Location, dpiScale),
                WpfToScreen(wpfRect.Size, dpiScale));
        }

        /// <summary>
        /// Rect'i ekran koordinatlarından WPF'e çevir
        /// </summary>
        public static Rect ScreenToWpf(Rect screenRect, double dpiScale)
        {
            return new Rect(
                ScreenToWpf(screenRect.Location, dpiScale),
                ScreenToWpf(screenRect.Size, dpiScale));
        }

        #endregion

        #region Window Helpers

        /// <summary>
        /// Pencereyi DPI-aware konumlandır
        /// </summary>
        public static void PositionWindowDpiAware(Window window, double screenX, double screenY)
        {
            var scale = GetDpiScaleForWindow(window);
            window.Left = ScreenToWpf(screenX, scale);
            window.Top = ScreenToWpf(screenY, scale);
        }

        /// <summary>
        /// Pencereyi DPI-aware boyutlandır
        /// </summary>
        public static void SizeWindowDpiAware(Window window, double screenWidth, double screenHeight)
        {
            var scale = GetDpiScaleForWindow(window);
            window.Width = ScreenToWpf(screenWidth, scale);
            window.Height = ScreenToWpf(screenHeight, scale);
        }

        /// <summary>
        /// DPI değişikliği event handler'ı için extension
        /// </summary>
        public static void HandleDpiChanged(Window window, System.Windows.DpiChangedEventArgs e)
        {
            CurrentDpi = e.NewDpi.PixelsPerInchX;
            CurrentScale = GetDpiScale(CurrentDpi);

            Log.Debug("[DPI] DPI değişti: {OldDpi} -> {NewDpi} (Scale: {Scale:P0})",
                e.OldDpi.PixelsPerInchX,
                e.NewDpi.PixelsPerInchX,
                CurrentScale);

            // Transform güncelle
            if (window.Content is FrameworkElement root)
            {
                var scaleTransform = new ScaleTransform(
                    e.NewDpi.PixelsPerInchX / e.OldDpi.PixelsPerInchX,
                    e.NewDpi.PixelsPerInchY / e.OldDpi.PixelsPerInchY);

                root.LayoutTransform = scaleTransform;
            }
        }

        #endregion

        #region Font Scaling

        /// <summary>
        /// DPI'ya göre font boyutunu ölçekle
        /// </summary>
        public static double ScaleFontSize(double baseFontSize, double dpiScale)
        {
            // Font'lar zaten DPI-aware olduğundan genellikle ölçekleme gerekmez
            // Ancak manuel ayarlama gerektiğinde kullanılabilir
            return baseFontSize;
        }

        /// <summary>
        /// DPI'ya göre icon boyutunu ölçekle
        /// </summary>
        public static double ScaleIconSize(double baseIconSize, double dpiScale)
        {
            // Icon'lar için ölçekleme önerilir
            return baseIconSize * dpiScale;
        }

        #endregion
    }

    /// <summary>
    /// DÜZELTME v18: DPI-aware overlay window extension
    /// </summary>
    public static class DpiAwareWindowExtensions
    {
        /// <summary>
        /// Pencereyi DPI-aware yap
        /// </summary>
        public static void MakeDpiAware(this Window window)
        {
            // DPI değişikliği event'ini dinle
            window.DpiChanged += (s, e) => DpiHelper.HandleDpiChanged(window, e);

            // İlk DPI değerini al
            window.Loaded += (s, e) =>
            {
                var scale = DpiHelper.GetDpiScaleForWindow(window);
                Log.Debug("[DPI] {WindowName} DPI Scale: {Scale:P0}",
                    window.GetType().Name, scale);
            };
        }

        /// <summary>
        /// Pencere konumunu DPI-aware şekilde al
        /// </summary>
        public static Point GetScreenPosition(this Window window)
        {
            var scale = DpiHelper.GetDpiScaleForWindow(window);
            return new Point(
                DpiHelper.WpfToScreen(window.Left, scale),
                DpiHelper.WpfToScreen(window.Top, scale));
        }

        /// <summary>
        /// Pencere boyutunu DPI-aware şekilde al
        /// </summary>
        public static Size GetScreenSize(this Window window)
        {
            var scale = DpiHelper.GetDpiScaleForWindow(window);
            return new Size(
                DpiHelper.WpfToScreen(window.ActualWidth, scale),
                DpiHelper.WpfToScreen(window.ActualHeight, scale));
        }
    }
}
