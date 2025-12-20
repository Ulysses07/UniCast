using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace UniCast.App.Views
{
    /// <summary>
    /// Uygulama açılış ekranı.
    /// DÜZELTME v50: App.xaml.cs'den kontrol edilebilir, gerçek progress gösterir.
    /// </summary>
    public partial class SplashWindow : Window
    {
        private Storyboard? _indeterminateStoryboard;
        private double _maxWidth;

        public SplashWindow()
        {
            InitializeComponent();

            // Versiyon bilgisini ayarla
            try
            {
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                VersionText.Text = $"v{version?.Major}.{version?.Minor}.{version?.Build}";
            }
            catch
            {
                VersionText.Text = "v1.0.0";
            }

            Loaded += SplashWindow_Loaded;
        }

        private void SplashWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Progress bar max width'i hesapla
            _maxWidth = ActualWidth - 80; // 40 margin each side

            // Indeterminate animation'ı hazırla
            _indeterminateStoryboard = (Storyboard)FindResource("IndeterminateAnimation");
        }

        /// <summary>
        /// Progress değerini günceller (0-100).
        /// </summary>
        public void SetProgress(int value, string status, string? subStatus = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetProgress(value, status, subStatus));
                return;
            }

            // Status text
            StatusText.Text = status;

            // Sub-status text
            if (!string.IsNullOrEmpty(subStatus))
                SubStatusText.Text = subStatus;

            // Progress bar
            if (value >= 0 && value <= 100)
            {
                // Determinate mode
                IndeterminateCanvas.Visibility = Visibility.Collapsed;
                ProgressFill.Visibility = Visibility.Visible;

                var targetWidth = (_maxWidth > 0 ? _maxWidth : 420) * (value / 100.0);

                // Animate progress
                var animation = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                ProgressFill.BeginAnimation(WidthProperty, animation);
            }
        }

        /// <summary>
        /// Indeterminate mode'a geçer (süresiz bekleme).
        /// </summary>
        public void SetIndeterminate(string status, string? subStatus = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetIndeterminate(status, subStatus));
                return;
            }

            StatusText.Text = status;

            if (!string.IsNullOrEmpty(subStatus))
                SubStatusText.Text = subStatus;

            // Indeterminate mode
            ProgressFill.Visibility = Visibility.Collapsed;
            IndeterminateCanvas.Visibility = Visibility.Visible;

            _indeterminateStoryboard?.Begin(this, true);
        }

        /// <summary>
        /// Sadece status text'i günceller.
        /// </summary>
        public void SetStatus(string status, string? subStatus = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetStatus(status, subStatus));
                return;
            }

            StatusText.Text = status;

            if (subStatus != null)
                SubStatusText.Text = subStatus;
        }

        /// <summary>
        /// Hata durumunu gösterir.
        /// </summary>
        public void SetError(string errorMessage)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetError(errorMessage));
                return;
            }

            StatusText.Text = "❌ Hata";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(233, 69, 96)); // #e94560

            SubStatusText.Text = errorMessage;
            SubStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(170, 170, 170));

            // Progress bar'ı kırmızı yap
            ProgressFill.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(233, 69, 96));

            // Animasyonları durdur
            _indeterminateStoryboard?.Stop(this);
        }

        /// <summary>
        /// Başarılı tamamlanma durumunu gösterir.
        /// </summary>
        public void SetComplete(string message = "Hazır!")
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetComplete(message));
                return;
            }

            StatusText.Text = $"✓ {message}";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(76, 175, 80)); // Green

            SubStatusText.Text = "";

            // Progress'i %100 yap
            SetProgress(100, StatusText.Text);

            // Animasyonları durdur
            _indeterminateStoryboard?.Stop(this);
        }
    }
}