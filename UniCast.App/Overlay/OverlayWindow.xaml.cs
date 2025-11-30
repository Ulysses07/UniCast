using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UniCast.App.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;



// Çakışma önlemek için alias
using CoreChatMessage = UniCast.Core.Chat.ChatMessage;
using CoreChatPlatform = UniCast.Core.Chat.ChatPlatform;

namespace UniCast.App.Overlay
{
    /// <summary>
    /// Overlay penceresi - yayın üzerinde gösterilen chat ve bilgiler
    /// </summary>
    public partial class OverlayWindow : Window
    {
        private const int MaxMessages = 50;

        private readonly ObservableCollection<ChatMessageViewModel> _messages = new();
        private readonly DispatcherTimer _uptimeTimer;
        private readonly DispatcherTimer _breakTimer;

        private DateTime _streamStartTime;
        private int _breakMinutesRemaining;
        private OverlayStatus _currentStatus = OverlayStatus.Offline;

        public OverlayWindow()
        {
            InitializeComponent();

            ChatMessages.ItemsSource = _messages;

            // Uptime timer
            _uptimeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _uptimeTimer.Tick += UptimeTimer_Tick;

            // Break timer
            _breakTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _breakTimer.Tick += BreakTimer_Tick;

            ReloadSettings();
        }

        #region Public Methods

        /// <summary>
        /// Ayarları yeniden yükle
        /// </summary>
        public void ReloadSettings()
        {
            var settings = SettingsStore.Current;

            Width = Math.Clamp(settings.OverlayWidth, 200, 1920);
            Height = Math.Clamp(settings.OverlayHeight, 150, 1080);
            Opacity = Math.Clamp(settings.OverlayOpacity, 0.1, 1.0);

            // Theme uygula
            ApplyTheme(settings.OverlayTheme);
        }

        /// <summary>
        /// Pencere boyutunu güncelle
        /// </summary>
        public void UpdateSize(double width, double height)
        {
            Width = Math.Clamp(width, 200, 1920);
            Height = Math.Clamp(height, 150, 1080);
        }

        /// <summary>
        /// Pencere pozisyonunu güncelle
        /// </summary>
        public void UpdatePosition(double x, double y)
        {
            Left = x;
            Top = y;
        }

        /// <summary>
        /// Chat mesajı göster
        /// </summary>
        public void ShowChatMessage(CoreChatMessage message)
        {
            Dispatcher.Invoke(() =>
            {
                var vm = new ChatMessageViewModel
                {
                    DisplayName = message.DisplayName ?? message.Username,
                    Message = message.Message,
                    Platform = message.Platform,
                    PlatformShort = GetPlatformShort(message.Platform),
                    PlatformColor = GetPlatformBrush(message.Platform),
                    Timestamp = message.Timestamp
                };

                _messages.Add(vm);

                // Limit mesaj sayısı
                while (_messages.Count > MaxMessages)
                {
                    _messages.RemoveAt(0);
                }

                // Auto-scroll
                if (ChatScrollViewer != null)
                {
                    ChatScrollViewer.ScrollToEnd();
                }
            });
        }

        /// <summary>
        /// Sistem mesajı göster
        /// </summary>
        public void ShowMessage(string message, string sender = "System")
        {
            Dispatcher.Invoke(() =>
            {
                var vm = new ChatMessageViewModel
                {
                    DisplayName = sender,
                    Message = message,
                    Platform = CoreChatPlatform.YouTube,
                    PlatformShort = "SYS",
                    PlatformColor = Brushes.Gray,
                    Timestamp = DateTime.Now
                };

                _messages.Add(vm);

                while (_messages.Count > MaxMessages)
                {
                    _messages.RemoveAt(0);
                }
            });
        }

        /// <summary>
        /// Mola modunu başlat
        /// </summary>
        public void StartBreakMode(int minutes)
        {
            _breakMinutesRemaining = minutes * 60; // saniyeye çevir

            Dispatcher.Invoke(() =>
            {
                if (BreakOverlay != null)
                {
                    BreakOverlay.Visibility = Visibility.Visible;
                }
                UpdateBreakTimer();
                _breakTimer.Start();
            });
        }

        /// <summary>
        /// Mola modunu durdur
        /// </summary>
        public void StopBreakMode()
        {
            _breakTimer.Stop();

            Dispatcher.Invoke(() =>
            {
                if (BreakOverlay != null)
                {
                    BreakOverlay.Visibility = Visibility.Collapsed;
                }
            });
        }

        /// <summary>
        /// İzleyici sayısını güncelle
        /// </summary>
        public void UpdateViewerCount(int count)
        {
            Dispatcher.Invoke(() =>
            {
                if (ViewerCount != null)
                {
                    ViewerCount.Text = $"👥 {count:N0}";
                }
            });
        }

        /// <summary>
        /// Yayın başlangıç zamanını ayarla
        /// </summary>
        public void SetStreamStartTime(DateTime startTime)
        {
            _streamStartTime = startTime;
            _uptimeTimer.Start();
        }

        /// <summary>
        /// Durumu ayarla
        /// </summary>
        public void SetStatus(OverlayStatus status)
        {
            _currentStatus = status;

            Dispatcher.Invoke(() =>
            {
                if (StatusIndicator != null)
                {
                    StatusIndicator.Fill = status switch
                    {
                        OverlayStatus.Live => Brushes.LimeGreen,
                        OverlayStatus.Connecting => Brushes.Yellow,
                        OverlayStatus.Error => Brushes.Red,
                        _ => Brushes.Gray
                    };
                }

                if (StatusText != null)
                {
                    StatusText.Text = status switch
                    {
                        OverlayStatus.Live => "CANLI",
                        OverlayStatus.Connecting => "BAĞLANIYOR",
                        OverlayStatus.Error => "HATA",
                        _ => "KAPALI"
                    };
                }
            });
        }

        #endregion

        #region Event Handlers

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void UptimeTimer_Tick(object? sender, EventArgs e)
        {
            var elapsed = DateTime.Now - _streamStartTime;

            Dispatcher.Invoke(() =>
            {
                if (UptimeText != null)
                {
                    UptimeText.Text = $"⏱ {elapsed:hh\\:mm\\:ss}";
                }
            });
        }

        private void BreakTimer_Tick(object? sender, EventArgs e)
        {
            _breakMinutesRemaining--;

            if (_breakMinutesRemaining <= 0)
            {
                StopBreakMode();
                return;
            }

            UpdateBreakTimer();
        }

        #endregion

        #region Private Methods

        private void UpdateBreakTimer()
        {
            var minutes = _breakMinutesRemaining / 60;
            var seconds = _breakMinutesRemaining % 60;

            Dispatcher.Invoke(() =>
            {
                if (BreakCountdown != null)
                {
                    BreakCountdown.Text = $"{minutes:D2}:{seconds:D2}";
                }
            });
        }

        private void ApplyTheme(string theme)
        {
            // Tema uygulaması - şimdilik basit
            // İleride genişletilebilir
        }

        private static string GetPlatformShort(CoreChatPlatform platform)
        {
            return platform switch
            {
                CoreChatPlatform.YouTube => "YT",
                CoreChatPlatform.Twitch => "TW",
                CoreChatPlatform.TikTok => "TT",
                CoreChatPlatform.Instagram => "IG",
                CoreChatPlatform.Facebook => "FB",
                CoreChatPlatform.Twitter => "X",
                CoreChatPlatform.Discord => "DC",
                CoreChatPlatform.Kick => "KK",
                _ => "?"
            };
        }

        private static Brush GetPlatformBrush(CoreChatPlatform platform)
        {
            return platform switch
            {
                CoreChatPlatform.YouTube => new SolidColorBrush(Color.FromRgb(255, 0, 0)),
                CoreChatPlatform.Twitch => new SolidColorBrush(Color.FromRgb(145, 70, 255)),
                CoreChatPlatform.TikTok => new SolidColorBrush(Color.FromRgb(0, 0, 0)),
                CoreChatPlatform.Instagram => new SolidColorBrush(Color.FromRgb(225, 48, 108)),
                CoreChatPlatform.Facebook => new SolidColorBrush(Color.FromRgb(24, 119, 242)),
                CoreChatPlatform.Twitter => new SolidColorBrush(Color.FromRgb(29, 161, 242)),
                CoreChatPlatform.Discord => new SolidColorBrush(Color.FromRgb(114, 137, 218)),
                CoreChatPlatform.Kick => new SolidColorBrush(Color.FromRgb(83, 252, 24)),
                _ => Brushes.White
            };
        }

        #endregion
    }

    /// <summary>
    /// Chat mesaj view model
    /// </summary>
    public class ChatMessageViewModel
    {
        public string DisplayName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public CoreChatPlatform Platform { get; set; }
        public string PlatformShort { get; set; } = string.Empty;
        public Brush PlatformColor { get; set; } = Brushes.White;
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Overlay durumu
    /// </summary>
    public enum OverlayStatus
    {
        Offline,
        Connecting,
        Live,
        Error
    }
}