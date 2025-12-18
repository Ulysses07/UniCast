using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;

namespace UniCast.App.Services
{
    /// <summary>
    /// Toast bildirim türleri
    /// </summary>
    public enum ToastType
    {
        Success,
        Error,
        Warning,
        Info
    }

    /// <summary>
    /// Tek bir toast bildirimi
    /// </summary>
    public class ToastItem : INotifyPropertyChanged
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public string Message { get; set; } = "";
        public ToastType Type { get; set; } = ToastType.Info;
        public string Icon => Type switch
        {
            ToastType.Success => "✓",
            ToastType.Error => "✕",
            ToastType.Warning => "⚠",
            ToastType.Info => "ℹ",
            _ => "●"
        };

        public SolidColorBrush Background => Type switch
        {
            ToastType.Success => new SolidColorBrush(Color.FromRgb(34, 139, 34)),   // Forest Green
            ToastType.Error => new SolidColorBrush(Color.FromRgb(178, 34, 34)),     // Firebrick
            ToastType.Warning => new SolidColorBrush(Color.FromRgb(184, 134, 11)),  // Dark Goldenrod
            ToastType.Info => new SolidColorBrush(Color.FromRgb(70, 130, 180)),     // Steel Blue
            _ => new SolidColorBrush(Colors.Gray)
        };

        private double _opacity = 1.0;
        public double Opacity
        {
            get => _opacity;
            set { _opacity = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Toast bildirim servisi - Singleton
    /// </summary>
    public class ToastService : INotifyPropertyChanged
    {
        private static readonly Lazy<ToastService> _instance = new(() => new ToastService());
        public static ToastService Instance => _instance.Value;

        public ObservableCollection<ToastItem> Toasts { get; } = new();

        private ToastService() { }

        /// <summary>
        /// Başarı bildirimi göster
        /// </summary>
        public void ShowSuccess(string message) => Show(message, ToastType.Success);

        /// <summary>
        /// Hata bildirimi göster
        /// </summary>
        public void ShowError(string message) => Show(message, ToastType.Error);

        /// <summary>
        /// Uyarı bildirimi göster
        /// </summary>
        public void ShowWarning(string message) => Show(message, ToastType.Warning);

        /// <summary>
        /// Bilgi bildirimi göster
        /// </summary>
        public void ShowInfo(string message) => Show(message, ToastType.Info);

        /// <summary>
        /// Toast bildirimi göster
        /// </summary>
        public void Show(string message, ToastType type = ToastType.Info, int durationMs = 3000)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                var toast = new ToastItem
                {
                    Message = message,
                    Type = type
                };

                Toasts.Add(toast);

                // Otomatik kaldır
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();

                    // Fade out animasyonu
                    var fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                    fadeTimer.Tick += (s2, e2) =>
                    {
                        toast.Opacity -= 0.1;
                        if (toast.Opacity <= 0)
                        {
                            fadeTimer.Stop();
                            Toasts.Remove(toast);
                        }
                    };
                    fadeTimer.Start();
                };
                timer.Start();

                // Maksimum 5 toast göster
                while (Toasts.Count > 5)
                {
                    Toasts.RemoveAt(0);
                }
            });
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}