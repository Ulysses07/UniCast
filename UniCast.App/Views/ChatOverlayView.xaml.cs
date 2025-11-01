using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace UniCast.App.Views
{
    public partial class ChatOverlayView : System.Windows.Controls.UserControl
    {
        private readonly List<(string author, string text, DateTime ts)> _messages = new();
        private readonly DropShadowEffect _shadow = new()
        {
            Color = Colors.Black,
            BlurRadius = 6,
            ShadowDepth = 0,
            Opacity = 0.75
        };

        public ChatOverlayView()
        {
            InitializeComponent();
            SizeChanged += (_, __) => Redraw();
        }

        public void Push(string author, string text)
        {
            _messages.Add((author, text, DateTime.UtcNow));
            if (_messages.Count > 20) _messages.RemoveAt(0);
            Redraw();
        }

        public void ClearOld(TimeSpan maxAge)
        {
            var now = DateTime.UtcNow;
            _messages.RemoveAll(m => (now - m.ts) > maxAge);
            Redraw();
        }

        private void Redraw()
        {
            Surface.Children.Clear();
            if (ActualWidth <= 0 || ActualHeight <= 0) return;

            double margin = 16;
            double y = ActualHeight - margin;

            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                var (author, text, _) = _messages[i];
                var bubble = DrawBubble($"{author}: {text}", MaxWidth: ActualWidth * 0.9);
                y -= bubble.Height + 8;
                if (y < 0) break;
                Canvas.SetLeft(bubble, margin);
                Canvas.SetTop(bubble, y);
                Surface.Children.Add(bubble);
            }
        }

        private Border DrawBubble(string text, double MaxWidth)
        {
            var tb = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                FontSize = 22,
                Effect = _shadow,
                MaxWidth = MaxWidth - 24
            };

            var bg = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(150, 20, 20, 20)),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 8, 12, 8),
                Child = tb
            };

            bg.Measure(new Size(MaxWidth, double.PositiveInfinity));
            bg.Arrange(new Rect(new Point(0, 0), bg.DesiredSize));
            return bg;
        }
    }
}
