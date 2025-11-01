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
            Color = System.Windows.Media.Colors.Black,
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
                var bubble = CreateBubble($"{author}: {text}", ActualWidth * 0.9);
                y -= bubble.Height + 8;
                if (y < 0) break;
                Canvas.SetLeft(bubble, margin);
                Canvas.SetTop(bubble, y);
                Surface.Children.Add(bubble);
            }
        }

        private Border CreateBubble(string text, double maxWidth)
        {
            var tb = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 22,
                Effect = _shadow,
                MaxWidth = maxWidth - 24
            };

            var border = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 20, 20, 20)),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 8, 12, 8),
                Child = tb
            };

            // Ölç ve yerleştir (WPF türleri tam adla)
            border.Measure(new System.Windows.Size(maxWidth, double.PositiveInfinity));
            border.Arrange(new Rect(new System.Windows.Point(0, 0), border.DesiredSize));
            return border;
        }
    }
}
