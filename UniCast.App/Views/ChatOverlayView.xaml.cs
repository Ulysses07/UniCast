using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows;

namespace UniCast.App.Views
{
    public partial class ChatOverlayView : UserControl
    {
        public ChatOverlayView()
        {
            InitializeComponent();
        }

        // YENİ METOT: Koordinatları günceller
        public void SetPosition(double x, double y)
        {
            Canvas.SetLeft(ChatContainer, x);
            Canvas.SetTop(ChatContainer, y);
        }

        public void AddMessage(string author, string message)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch // Kutuya yayıl
            };

            var textBlock = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            };

            textBlock.Inlines.Add(new Run(author + ": ") { FontWeight = FontWeights.Bold, Foreground = Brushes.Gold });
            textBlock.Inlines.Add(new Run(message));

            border.Child = textBlock;
            MessagePanel.Children.Add(border);

            if (MessagePanel.Children.Count > 8) // Çok uzamasın
            {
                MessagePanel.Children.RemoveAt(0);
            }
        }
    }
}