using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;
using UniCast.App.Services;
using UniCast.Core.Chat;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace UniCast.App.Overlay
{
    /// <summary>
    /// Overlay penceresi - Stream üstünde gösterilen chat ve bilgi paneli.
    /// </summary>
    public partial class OverlayWindow : Window
    {
        private readonly ObservableCollection<ChatMessageViewModel> _messages = new();
        private readonly DispatcherTimer _uptimeTimer;
        private readonly DispatcherTimer _breakTimer;

        private DateTime _streamStartTime;
        private int _breakRemainingSeconds;
        private bool _isBreakMode;

        private const int MaxMessages = 50;

        public OverlayWindow()
        {
            InitializeComponent();

            ChatItemsControl.ItemsSource = _messages;

            // Uptime timer
            _streamStartTime = DateTime.UtcNow;
            _uptimeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _uptimeTimer.Tick += UptimeTimer_Tick;
            _uptimeTimer.Start();

            // Break timer
            _breakTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _breakTimer.Tick += BreakTimer_Tick;

            // Ayarları yükle
            ReloadSettings();

            Log.Debug("[OverlayWindow] Initialized");
        }

        #region Public Methods

        /// <summary>
        /// Ayarları yeniden yükler.
        /// </summary>
        public void ReloadSettings()
        {
            try
            {
                var settings = SettingsStore.Data;

                Width = settings.OverlayWidth;
                Height = settings.OverlayHeight;
                MainBorder.Opacity = settings.OverlayOpacity;

                // Tema
                if (settings.OverlayTheme == "Light")
                {
                    MainBorder.Background = new SolidColorBrush(Color.FromArgb(230, 245, 245, 250));
                }
                else
                {
                    MainBorder.Background = new SolidColorBrush(Color.FromArgb(230, 24, 24, 37));
                }

                Log.Debug("[OverlayWindow] Ayarlar yeniden yüklendi");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[OverlayWindow] Ayar yükleme hatası");
            }
        }

        /// <summary>
        /// Boyutu günceller.
        /// </summary>
        public void UpdateSize(double width, double height)
        {
            Width = Math.Clamp(width, 200, 1920);
            Height = Math.Clamp(height, 150, 1080);
        }

        /// <summary>
        /// Pozisyonu günceller.
        /// </summary>
        public void UpdatePosition(int x, int y)
        {
            Left = x;
            Top = y;
        }

        /// <summary>
        /// Chat mesajı gösterir.
        /// </summary>
        public void ShowChatMessage(ChatMessage message)
        {
            if (message == null)
                return;

            Dispatcher.Invoke(() =>
            {
                try
                {
                    var vm = new ChatMessageViewModel
                    {
                        DisplayName = message.DisplayName,
                        Message = message.Message,
                        Platform = message.Platform,
                        PlatformShort = GetPlatformShort(message.Platform),
                        PlatformColor = GetPlatformBrush(message.Platform),
                        Timestamp = message.Timestamp
                    };

                    _messages.Add(vm);

                    // Limit kontrolü
                    while (_messages.Count > MaxMessages)
                    {
                        _messages.RemoveAt(0);
                    }

                    // Otomatik scroll
                    ChatScrollViewer.ScrollToEnd();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[OverlayWindow] Mesaj gösterme hatası");
                }
            });
        }

        /// <summary>
        /// Bildirim mesajı gösterir.
        /// </summary>
        public void ShowMessage(string message, string? sender = null)
        {
            var chatMessage = new ChatMessage
            {
                Platform = ChatPlatform.Unknown,
                DisplayName = sender ?? "System",
                Message = message,
                Type = ChatMessageType.System
            };

            ShowChatMessage(chatMessage);
        }

        /// <summary>
        /// Mola modunu başlatır.
        /// </summary>
        public void StartBreakMode(int minutes)
        {
            _isBreakMode = true;
            _breakRemainingSeconds = minutes * 60;

            BreakOverlay.Visibility = Visibility.Visible;
            UpdateBreakTimer();
            _breakTimer.Start();

            Log.Information("[OverlayWindow] Mola modu başlatıldı: {Minutes} dakika", minutes);
        }

        /// <summary>
        /// Mola modunu durdurur.
        /// </summary>
        public void StopBreakMode()
        {
            _isBreakMode = false;
            _breakTimer.Stop();
            BreakOverlay.Visibility = Visibility.Collapsed;

            Log.Information("[OverlayWindow] Mola modu durduruldu");
        }

        /// <summary>
        /// İzleyici sayısını günceller.
        /// </summary>
        public void UpdateViewerCount(int count)
        {
            Dispatcher.Invoke(() =>
            {
                ViewerCountText.Text = $"👥 {count:N0}";
            });
        }

        /// <summary>
        /// Stream başlangıç zamanını ayarlar.
        /// </summary>
        public void SetStreamStartTime(DateTime startTime)
        {
            _streamStartTime = startTime;
        }

        /// <summary>
        /// Durum göstergesini günceller.
        /// </summary>
        public void SetStatus(OverlayStatus status)
        {
            Dispatcher.Invoke(() =>
            {
                StatusIndicator.Fill = status switch
                {
                    OverlayStatus.Live => new SolidColorBrush(Color.FromRgb(16, 185, 129)), // Yeşil
                    OverlayStatus.Connecting => new SolidColorBrush(Color.FromRgb(251, 191, 36)), // Sarı
                    OverlayStatus.Error => new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Kırmızı
                    _ => new SolidColorBrush(Color.FromRgb(107, 114, 128)) // Gri
                };

                TitleText.Text = status switch
                {
                    OverlayStatus.Live => "🔴 LIVE",
                    OverlayStatus.Connecting => "Bağlanıyor...",
                    OverlayStatus.Error => "Hata",
                    _ => "UniCast"
                };
            });
        }

        #endregion

        #region Event Handlers

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void UptimeTimer_Tick(object? sender, EventArgs e)
        {
            var uptime = DateTime.UtcNow - _streamStartTime;
            UptimeText.Text = $"⏱ {uptime:hh\\:mm\\:ss}";
        }

        private void BreakTimer_Tick(object? sender, EventArgs e)
        {
            if (_breakRemainingSeconds > 0)
            {
                _breakRemainingSeconds--;
                UpdateBreakTimer();
            }
            else
            {
                StopBreakMode();
            }
        }

        private void UpdateBreakTimer()
        {
            var minutes = _breakRemainingSeconds / 60;
            var seconds = _breakRemainingSeconds % 60;
            BreakTimerText.Text = $"{minutes:D2}:{seconds:D2}";
        }

        #endregion

        #region Helpers

        private static string GetPlatformShort(ChatPlatform platform)
        {
            return platform switch
            {
                ChatPlatform.YouTube => "YT",
                ChatPlatform.Twitch => "TW",
                ChatPlatform.TikTok => "TT",
                ChatPlatform.Instagram => "IG",
                ChatPlatform.Facebook => "FB",
                ChatPlatform.Twitter => "X",
                ChatPlatform.Discord => "DC",
                ChatPlatform.Kick => "KK",
                _ => "?"
            };
        }

        private static Brush GetPlatformBrush(ChatPlatform platform)
        {
            return platform switch
            {
                ChatPlatform.YouTube => new SolidColorBrush(Color.FromRgb(255, 0, 0)),
                ChatPlatform.Twitch => new SolidColorBrush(Color.FromRgb(145, 70, 255)),
                ChatPlatform.TikTok => new SolidColorBrush(Color.FromRgb(0, 0, 0)),
                ChatPlatform.Instagram => new SolidColorBrush(Color.FromRgb(225, 48, 108)),
                ChatPlatform.Facebook => new SolidColorBrush(Color.FromRgb(66, 103, 178)),
                ChatPlatform.Twitter => new SolidColorBrush(Color.FromRgb(29, 161, 242)),
                ChatPlatform.Discord => new SolidColorBrush(Color.FromRgb(114, 137, 218)),
                ChatPlatform.Kick => new SolidColorBrush(Color.FromRgb(83, 252, 24)),
                _ => new SolidColorBrush(Color.FromRgb(128, 128, 128))
            };
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            _uptimeTimer.Stop();
            _breakTimer.Stop();
            base.OnClosed(e);
        }
    }

    /// <summary>
    /// Chat mesajı view model.
    /// </summary>
    public class ChatMessageViewModel
    {
        public string DisplayName { get; set; } = "";
        public string Message { get; set; } = "";
        public ChatPlatform Platform { get; set; }
        public string PlatformShort { get; set; } = "";
        public Brush PlatformColor { get; set; } = Brushes.Gray;
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Overlay durum türleri.
    /// </summary>
    public enum OverlayStatus
    {
        Offline,
        Connecting,
        Live,
        Error
    }
}