using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
// Brushes, Color vb. için using eklemiyoruz, çakışmayı önlemek için tam adını yazacağız.

namespace UniCast.App.Views
{
    // HATA DÜZELTME: 'System.Windows.Controls.UserControl' (WPF) olduğunu belirtiyoruz.
    public partial class ChatOverlayView : System.Windows.Controls.UserControl
    {
        public ChatOverlayView()
        {
            InitializeComponent();
        }

        public void SetPosition(double x, double y)
        {
            // Canvas, System.Windows.Controls altındadır
            Canvas.SetLeft(ChatContainer, x);
            Canvas.SetTop(ChatContainer, y);
        }

        public void AddMessage(string author, string message)
        {
            var border = new Border
            {
                // HATA DÜZELTME: System.Windows.Media.Color
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 0, 0, 0)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 2, 0, 2),

                // HATA DÜZELTME: System.Windows.HorizontalAlignment
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
            };

            var textBlock = new TextBlock
            {
                // HATA DÜZELTME: System.Windows.Media.Brushes
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            };

            textBlock.Inlines.Add(new Run(author + ": ")
            {
                FontWeight = FontWeights.Bold,
                // HATA DÜZELTME: System.Windows.Media.Brushes
                Foreground = System.Windows.Media.Brushes.Gold
            });

            textBlock.Inlines.Add(new Run(message));

            border.Child = textBlock;
            MessagePanel.Children.Add(border);

            if (MessagePanel.Children.Count > 8)
            {
                MessagePanel.Children.RemoveAt(0);
            }
        }
    }
}