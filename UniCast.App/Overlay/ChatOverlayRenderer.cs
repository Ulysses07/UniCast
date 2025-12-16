using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using UniCast.Core.Chat;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using Size = System.Windows.Size;

namespace UniCast.App.Overlay
{
    /// <summary>
    /// Chat mesajlarını stream overlay'i için render eder.
    /// Şeffaf arka plan ile video üzerine composite edilebilir frame'ler üretir.
    /// 
    /// Özellikler:
    /// - Platform bazlı renkli username'ler
    /// - Animasyonlu mesaj girişi
    /// - Otomatik mesaj temizleme (max limit)
    /// - Özelleştirilebilir font, boyut, pozisyon
    /// </summary>
    public sealed class ChatOverlayRenderer : IDisposable
    {
        #region Constants

        private const int DEFAULT_MAX_MESSAGES = 8;
        private const int DEFAULT_MESSAGE_LIFETIME_SECONDS = 30;
        private const double DEFAULT_FONT_SIZE = 18;
        private const double DEFAULT_OPACITY = 0.9;

        #endregion

        #region Fields

        private readonly Canvas _canvas;
        private readonly ConcurrentQueue<ChatOverlayMessage> _messageQueue;
        private readonly List<ChatOverlayMessage> _visibleMessages;
        private readonly object _renderLock = new();

        private RenderTargetBitmap? _renderTarget;
        private byte[]? _frameBuffer;

        private bool _disposed;

        #endregion

        #region Properties

        /// <summary>Overlay genişliği</summary>
        public int Width { get; private set; }

        /// <summary>Overlay yüksekliği</summary>
        public int Height { get; private set; }

        /// <summary>Maksimum görünür mesaj sayısı</summary>
        public int MaxVisibleMessages { get; set; } = DEFAULT_MAX_MESSAGES;

        /// <summary>Mesaj görünürlük süresi (saniye)</summary>
        public int MessageLifetimeSeconds { get; set; } = DEFAULT_MESSAGE_LIFETIME_SECONDS;

        /// <summary>Font boyutu</summary>
        public double FontSize { get; set; } = DEFAULT_FONT_SIZE;

        /// <summary>Overlay şeffaflığı (0-1)</summary>
        public double Opacity { get; set; } = DEFAULT_OPACITY;

        /// <summary>Overlay pozisyonu</summary>
        public ChatOverlayPosition Position { get; set; } = ChatOverlayPosition.BottomLeft;

        /// <summary>Arka plan şeffaf mı</summary>
        public bool TransparentBackground { get; set; } = true;

        /// <summary>Arka plan rengi (TransparentBackground=false ise)</summary>
        public Color BackgroundColor { get; set; } = Color.FromArgb(180, 0, 0, 0);

        /// <summary>Gölge efekti aktif mi</summary>
        public bool EnableShadow { get; set; } = true;

        /// <summary>Overlay aktif mi</summary>
        public bool IsEnabled { get; set; } = true;

        #endregion

        #region Constructor

        public ChatOverlayRenderer(int width, int height)
        {
            Width = width;
            Height = height;

            _canvas = new Canvas
            {
                Width = width,
                Height = height,
                Background = Brushes.Transparent,
                ClipToBounds = true
            };

            _messageQueue = new ConcurrentQueue<ChatOverlayMessage>();
            _visibleMessages = new List<ChatOverlayMessage>();

            InitializeRenderTarget();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Yeni chat mesajı ekle
        /// </summary>
        public void AddMessage(ChatMessage message)
        {
            if (!IsEnabled || _disposed) return;

            var overlayMessage = new ChatOverlayMessage
            {
                Id = message.Id,
                Username = message.DisplayName ?? message.Username,
                Text = message.Message,
                Platform = message.Platform,
                Timestamp = DateTime.Now,
                Color = GetPlatformColor(message.Platform)
            };

            _messageQueue.Enqueue(overlayMessage);
        }

        /// <summary>
        /// Overlay boyutunu güncelle
        /// </summary>
        public void Resize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            Width = width;
            Height = height;

            _canvas.Width = width;
            _canvas.Height = height;

            InitializeRenderTarget();
        }

        /// <summary>
        /// Mevcut frame'i render et ve byte array olarak döndür
        /// </summary>
        public byte[]? RenderFrame()
        {
            if (!IsEnabled || _disposed) return null;

            lock (_renderLock)
            {
                try
                {
                    // Yeni mesajları işle
                    ProcessMessageQueue();

                    // Eski mesajları temizle
                    CleanupExpiredMessages();

                    // Canvas'ı güncelle
                    UpdateCanvas();

                    // Render et
                    return RenderToBytes();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ChatOverlayRenderer] Render error: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Tüm mesajları temizle
        /// </summary>
        public void Clear()
        {
            lock (_renderLock)
            {
                while (_messageQueue.TryDequeue(out _)) { }
                _visibleMessages.Clear();
                _canvas.Children.Clear();
            }
        }

        #endregion

        #region Private Methods

        private void InitializeRenderTarget()
        {
            _renderTarget = new RenderTargetBitmap(
                Width, Height,
                96, 96,
                PixelFormats.Pbgra32);

            var stride = Width * 4;
            _frameBuffer = new byte[stride * Height];
        }

        private void ProcessMessageQueue()
        {
            while (_messageQueue.TryDequeue(out var message))
            {
                _visibleMessages.Add(message);

                // Maksimum mesaj sayısını aş
                while (_visibleMessages.Count > MaxVisibleMessages)
                {
                    _visibleMessages.RemoveAt(0);
                }
            }
        }

        private void CleanupExpiredMessages()
        {
            var now = DateTime.Now;
            var expireTime = TimeSpan.FromSeconds(MessageLifetimeSeconds);

            _visibleMessages.RemoveAll(m => (now - m.Timestamp) > expireTime);
        }

        private void UpdateCanvas()
        {
            _canvas.Children.Clear();

            if (_visibleMessages.Count == 0) return;

            // Arka plan (opsiyonel)
            if (!TransparentBackground)
            {
                var bg = new System.Windows.Shapes.Rectangle
                {
                    Width = Width,
                    Height = Height,
                    Fill = new SolidColorBrush(BackgroundColor)
                };
                _canvas.Children.Add(bg);
            }

            // Mesajları pozisyona göre yerleştir
            double yOffset = CalculateStartY();
            double xOffset = CalculateStartX();
            double lineHeight = FontSize * 1.8;

            foreach (var msg in _visibleMessages)
            {
                var messagePanel = CreateMessagePanel(msg);

                Canvas.SetLeft(messagePanel, xOffset);
                Canvas.SetTop(messagePanel, yOffset);

                _canvas.Children.Add(messagePanel);

                // Sonraki mesaj pozisyonu
                if (Position == ChatOverlayPosition.TopLeft || Position == ChatOverlayPosition.TopRight)
                {
                    yOffset += lineHeight;
                }
                else
                {
                    yOffset -= lineHeight;
                }
            }
        }

        private StackPanel CreateMessagePanel(ChatOverlayMessage msg)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Opacity = Opacity
            };

            // Platform badge
            var badge = new TextBlock
            {
                Text = GetPlatformBadge(msg.Platform),
                FontSize = FontSize * 0.8,
                Foreground = new SolidColorBrush(msg.Color),
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            // Username
            var username = new TextBlock
            {
                Text = msg.Username,
                FontSize = FontSize,
                Foreground = new SolidColorBrush(msg.Color),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 8, 0)
            };

            // Message text
            var text = new TextBlock
            {
                Text = msg.Text,
                FontSize = FontSize,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.NoWrap,
                MaxWidth = Width * 0.7
            };

            // Gölge efekti
            if (EnableShadow)
            {
                var shadow = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 3,
                    ShadowDepth = 1,
                    Opacity = 0.8
                };
                panel.Effect = shadow;
            }

            panel.Children.Add(badge);
            panel.Children.Add(username);
            panel.Children.Add(text);

            return panel;
        }

        private double CalculateStartY()
        {
            double padding = 20;
            double lineHeight = FontSize * 1.8;
            int messageCount = Math.Min(_visibleMessages.Count, MaxVisibleMessages);

            return Position switch
            {
                ChatOverlayPosition.TopLeft or ChatOverlayPosition.TopRight => padding,
                ChatOverlayPosition.BottomLeft or ChatOverlayPosition.BottomRight =>
                    Height - padding - lineHeight + (messageCount - 1) * lineHeight,
                ChatOverlayPosition.Center => (Height - messageCount * lineHeight) / 2,
                _ => Height - padding - lineHeight
            };
        }

        private double CalculateStartX()
        {
            double padding = 20;

            return Position switch
            {
                ChatOverlayPosition.TopRight or ChatOverlayPosition.BottomRight => Width * 0.3,
                ChatOverlayPosition.Center => Width * 0.15,
                _ => padding
            };
        }

        private byte[]? RenderToBytes()
        {
            if (_renderTarget == null || _frameBuffer == null) return null;

            // Canvas'ı measure ve arrange et
            _canvas.Measure(new Size(Width, Height));
            _canvas.Arrange(new Rect(0, 0, Width, Height));
            _canvas.UpdateLayout();

            // Render
            _renderTarget.Clear();
            _renderTarget.Render(_canvas);

            // Pixel data'yı kopyala
            var stride = Width * 4;
            _renderTarget.CopyPixels(_frameBuffer, stride, 0);

            return _frameBuffer;
        }

        private static Color GetPlatformColor(ChatPlatform platform)
        {
            return platform switch
            {
                ChatPlatform.YouTube => Color.FromRgb(255, 0, 0),
                ChatPlatform.Twitch => Color.FromRgb(145, 70, 255),
                ChatPlatform.TikTok => Color.FromRgb(0, 242, 234),
                ChatPlatform.Instagram => Color.FromRgb(225, 48, 108),
                ChatPlatform.Facebook => Color.FromRgb(24, 119, 242),
                ChatPlatform.Twitter => Color.FromRgb(29, 161, 242),
                ChatPlatform.Discord => Color.FromRgb(114, 137, 218),
                ChatPlatform.Kick => Color.FromRgb(83, 252, 24),
                _ => Colors.White
            };
        }

        private static string GetPlatformBadge(ChatPlatform platform)
        {
            return platform switch
            {
                ChatPlatform.YouTube => "▶",
                ChatPlatform.Twitch => "◆",
                ChatPlatform.TikTok => "♪",
                ChatPlatform.Instagram => "◉",
                ChatPlatform.Facebook => "●",
                ChatPlatform.Twitter => "✕",
                ChatPlatform.Discord => "◎",
                ChatPlatform.Kick => "◈",
                _ => "○"
            };
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Clear();
            _renderTarget = null;
            _frameBuffer = null;
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Overlay mesaj modeli
    /// </summary>
    public class ChatOverlayMessage
    {
        public string Id { get; set; } = "";
        public string Username { get; set; } = "";
        public string Text { get; set; } = "";
        public ChatPlatform Platform { get; set; }
        public DateTime Timestamp { get; set; }
        public Color Color { get; set; }
    }

    /// <summary>
    /// Overlay pozisyon seçenekleri
    /// </summary>
    public enum ChatOverlayPosition
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
        Center
    }

    #endregion
}