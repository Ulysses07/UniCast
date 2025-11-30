using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace UniCast.App.Views
{
    /// <summary>
    /// Uygulama açılış ekranı.
    /// Yükleme durumunu gösterir.
    /// </summary>
    public partial class SplashWindow : Window
    {
        private readonly DispatcherTimer _animationTimer;
        private double _progress;

        public SplashWindow()
        {
            InitializeComponent();

            // Loading bar animasyonu
            _animationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _animationTimer.Tick += AnimationTimer_Tick;
            _animationTimer.Start();

            // Fade-in animasyonu
            Opacity = 0;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
            BeginAnimation(OpacityProperty, fadeIn);
        }

        private void AnimationTimer_Tick(object? sender, EventArgs e)
        {
            _progress += 2;

            if (_progress > 100)
                _progress = 0;

            // Loading bar genişliğini güncelle
            var maxWidth = LoadingBar.ActualWidth > 0 ?
                ((Grid)LoadingBar.Parent).ActualWidth : 320;

            LoadingBar.Width = (maxWidth * Math.Min(_progress, 100)) / 100;
        }

        /// <summary>
        /// Durum metnini günceller.
        /// </summary>
        public void UpdateStatus(string status)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = status;
            });
        }

        /// <summary>
        /// İlerleme yüzdesini günceller.
        /// </summary>
        public void UpdateProgress(double percentage)
        {
            Dispatcher.Invoke(() =>
            {
                _progress = Math.Clamp(percentage, 0, 100);

                var maxWidth = ((Grid)LoadingBar.Parent).ActualWidth;
                LoadingBar.Width = (maxWidth * _progress) / 100;
            });
        }

        /// <summary>
        /// Animasyonlu kapatma.
        /// </summary>
        public new void Close()
        {
            _animationTimer.Stop();

            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
            fadeOut.Completed += (s, e) => base.Close();
            BeginAnimation(OpacityProperty, fadeOut);
        }

        protected override void OnClosed(EventArgs e)
        {
            _animationTimer.Stop();
            base.OnClosed(e);
        }
    }
}