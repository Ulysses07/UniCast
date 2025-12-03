using Serilog;
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using UniCast.Core.Chat;
using UniCast.Core.Chat.Ingestors;
using UniCast.Core.Services;
using Color = System.Windows.Media.Color;

namespace UniCast.App
{
    /// <summary>
    /// DÜZELTME v18: Platform Connection Status UI yönetimi
    /// MainWindow partial class - Status bar güncellemeleri
    /// </summary>
    public partial class MainWindow
    {
        #region Status Colors

        private static readonly SolidColorBrush DisconnectedColor = new(Color.FromRgb(102, 102, 102)); // #666
        private static readonly SolidColorBrush ConnectingColor = new(Color.FromRgb(255, 193, 7));     // Amber
        private static readonly SolidColorBrush ConnectedColor = new(Color.FromRgb(76, 175, 80));      // Green
        private static readonly SolidColorBrush ErrorColor = new(Color.FromRgb(244, 67, 54));          // Red
        private static readonly SolidColorBrush ReconnectingColor = new(Color.FromRgb(255, 152, 0));   // Orange

        #endregion

        #region Platform Status Fields

        private DispatcherTimer? _statusUpdateTimer;
        private int _messagesPerMinute;
        private int _messageCountThisMinute;
        private DateTime _lastMinuteReset = DateTime.UtcNow;

        #endregion

        #region Status Initialization

        /// <summary>
        /// DÜZELTME v18: Status bar'ı başlat
        /// </summary>
        private void InitializeStatusBar()
        {
            // DÜZELTME v20: AppConstants kullanımı
            _statusUpdateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(AppConstants.Intervals.StatusUpdateSeconds)
            };
            _statusUpdateTimer.Tick += OnStatusUpdateTick;
            _statusUpdateTimer.Start();

            // Ingestor state change event'lerini bağla
            WireUpIngestorStateEvents();

            Log.Debug("[MainWindow] Status bar başlatıldı");
        }

        /// <summary>
        /// Ingestor state change event'lerini bağla
        /// </summary>
        private void WireUpIngestorStateEvents()
        {
            if (_ytIngestor != null)
                _ytIngestor.StateChanged += OnIngestorStateChanged;

            if (_twitchIngestor != null)
                _twitchIngestor.StateChanged += OnIngestorStateChanged;

            if (_tikTokIngestor != null)
                _tikTokIngestor.StateChanged += OnIngestorStateChanged;

            if (_instagramIngestor != null)
                _instagramIngestor.StateChanged += OnIngestorStateChanged;

            if (_facebookIngestor != null)
                _facebookIngestor.StateChanged += OnIngestorStateChanged;
        }

        #endregion

        #region Status Updates

        /// <summary>
        /// Timer tick - periyodik güncelleme
        /// </summary>
        private void OnStatusUpdateTick(object? sender, EventArgs e)
        {
            try
            {
                // Mesaj/dakika hesapla
                UpdateMessageStats();

                // Stream durumunu güncelle
                UpdateStreamStatus();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MainWindow] Status güncelleme hatası");
            }
        }

        /// <summary>
        /// Ingestor state değişikliğinde çağrılır
        /// </summary>
        private void OnIngestorStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (sender is IChatIngestor ingestor)
                    {
                        UpdatePlatformIndicator(ingestor.Platform, e.NewState, e.Message);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[MainWindow] Platform status güncelleme hatası");
                }
            });
        }

        /// <summary>
        /// DÜZELTME v18: Platform göstergesini güncelle
        /// </summary>
        private void UpdatePlatformIndicator(ChatPlatform platform, ConnectionState state, string? error)
        {
            var (indicator, statusBorder) = platform switch
            {
                ChatPlatform.YouTube => (YouTubeIndicator, YouTubeStatus),
                ChatPlatform.Twitch => (TwitchIndicator, TwitchStatus),
                ChatPlatform.TikTok => (TikTokIndicator, TikTokStatus),
                ChatPlatform.Instagram => (InstagramIndicator, InstagramStatus),
                ChatPlatform.Facebook => (FacebookIndicator, FacebookStatus),
                _ => (null, null)
            };

            if (indicator == null || statusBorder == null)
                return;

            // Renk ve tooltip güncelle
            var (color, tooltip) = state switch
            {
                ConnectionState.Disconnected => (DisconnectedColor, $"{platform}: Bağlı değil"),
                ConnectionState.Connecting => (ConnectingColor, $"{platform}: Bağlanıyor..."),
                ConnectionState.Connected => (ConnectedColor, $"{platform}: Bağlı ✓"),
                ConnectionState.Reconnecting => (ReconnectingColor, $"{platform}: Yeniden bağlanıyor..."),
                ConnectionState.Error => (ErrorColor, $"{platform}: Hata - {error ?? "Bilinmeyen hata"}"),
                _ => (DisconnectedColor, $"{platform}: Bilinmiyor")
            };

            indicator.Fill = color;
            statusBorder.ToolTip = tooltip;

            Log.Debug("[MainWindow] {Platform} status: {State}", platform, state);
        }

        /// <summary>
        /// Mesaj istatistiklerini güncelle
        /// </summary>
        private void UpdateMessageStats()
        {
            var now = DateTime.UtcNow;

            // Dakika reset
            if ((now - _lastMinuteReset).TotalMinutes >= 1)
            {
                _messagesPerMinute = _messageCountThisMinute;
                _messageCountThisMinute = 0;
                _lastMinuteReset = now;
            }

            ChatStatsText.Text = $"💬 {_messagesPerMinute} mesaj/dk";
        }

        /// <summary>
        /// Stream durumunu güncelle
        /// </summary>
        private void UpdateStreamStatus()
        {
            try
            {
                var isStreaming = StreamController.Instance.IsRunning;

                if (isStreaming)
                {
                    StreamStatusIndicator.Fill = ConnectedColor;
                    StreamStatusText.Text = "🔴 CANLI";
                    StreamStatusText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                }
                else
                {
                    StreamStatusIndicator.Fill = DisconnectedColor;
                    StreamStatusText.Text = "Hazır";
                    StreamStatusText.Foreground = (SolidColorBrush)FindResource("TextMuted");
                }
            }
            catch
            {
                // StreamController erişim hatası - sessizce devam et
            }
        }

        /// <summary>
        /// Chat mesajı alındığında sayacı artır
        /// </summary>
        private void OnChatMessageReceivedForStats(ChatMessage message)
        {
            _messageCountThisMinute++;
        }

        #endregion

        #region Public Status Methods

        /// <summary>
        /// Tüm platform durumlarını güncelle
        /// </summary>
        public void RefreshAllPlatformStatuses()
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_ytIngestor != null)
                    UpdatePlatformIndicator(ChatPlatform.YouTube, _ytIngestor.State, _ytIngestor.LastError);

                if (_twitchIngestor != null)
                    UpdatePlatformIndicator(ChatPlatform.Twitch, _twitchIngestor.State, _twitchIngestor.LastError);

                if (_tikTokIngestor != null)
                    UpdatePlatformIndicator(ChatPlatform.TikTok, _tikTokIngestor.State, _tikTokIngestor.LastError);

                if (_instagramIngestor != null)
                    UpdatePlatformIndicator(ChatPlatform.Instagram, _instagramIngestor.State, _instagramIngestor.LastError);

                if (_facebookIngestor != null)
                    UpdatePlatformIndicator(ChatPlatform.Facebook, _facebookIngestor.State, _facebookIngestor.LastError);
            });
        }

        /// <summary>
        /// Viewer sayısını güncelle (platform API'lerinden alınacak)
        /// </summary>
        public void UpdateViewerCount(int? count)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ViewerCountText.Text = count.HasValue ? $"👁 {count:N0}" : "👁 --";
            });
        }

        #endregion

        #region Status Cleanup

        /// <summary>
        /// Status bar kaynaklarını temizle
        /// </summary>
        private void CleanupStatusBar()
        {
            try
            {
                _statusUpdateTimer?.Stop();
                _statusUpdateTimer = null;

                // Event'leri temizle
                if (_ytIngestor != null)
                    _ytIngestor.StateChanged -= OnIngestorStateChanged;
                if (_twitchIngestor != null)
                    _twitchIngestor.StateChanged -= OnIngestorStateChanged;
                if (_tikTokIngestor != null)
                    _tikTokIngestor.StateChanged -= OnIngestorStateChanged;
                if (_instagramIngestor != null)
                    _instagramIngestor.StateChanged -= OnIngestorStateChanged;
                if (_facebookIngestor != null)
                    _facebookIngestor.StateChanged -= OnIngestorStateChanged;

                Log.Debug("[MainWindow] Status bar temizlendi");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MainWindow] Status bar temizleme hatası");
            }
        }

        #endregion
    }
}