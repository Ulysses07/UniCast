using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace UniCast.App.Views
{
    public partial class ChatOverlayView : System.Windows.Controls.UserControl
    {
        public ChatOverlayView()
        {
            InitializeComponent();
        }

        public void SetPosition(double x, double y)
        {
            Canvas.SetLeft(ChatContainer, x);
            Canvas.SetTop(ChatContainer, y);
        }

        // EKSİK OLAN METOT BU:
        public void SetWidth(double width)
        {
            ChatContainer.Width = width;
        }

        public void AddMessage(string author, string message)
        {
            var border = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 0, 0, 0)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 2, 0, 2),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
            };

            var textBlock = new TextBlock
            {
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            };

            textBlock.Inlines.Add(new Run(author + ": ")
            {
                FontWeight = FontWeights.Bold,
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