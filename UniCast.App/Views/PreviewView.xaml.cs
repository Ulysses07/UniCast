using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniCast.App.Services; // SettingsStore için

namespace UniCast.App.Views
{
    public partial class PreviewView : UserControl
    {
        private bool _isDragging = false;
        private Point _startPoint;
        private double _startX, _startY;

        public PreviewView(object? viewModel = null) // Constructor Injection uyumu
        {
            InitializeComponent();
            if (viewModel != null) DataContext = viewModel;

            // Başlangıçta kayıtlı konumu yükle
            var s = SettingsStore.Load();
            Canvas.SetLeft(DraggableChatBox, s.OverlayX);
            Canvas.SetTop(DraggableChatBox, s.OverlayY);
        }

        private void ChatBox_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            _startPoint = e.GetPosition(OverlayCanvas);
            _startX = Canvas.GetLeft(DraggableChatBox);
            _startY = Canvas.GetTop(DraggableChatBox);
            DraggableChatBox.CaptureMouse();
        }

        private void ChatBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                var currentPoint = e.GetPosition(OverlayCanvas);
                var offsetX = currentPoint.X - _startPoint.X;
                var offsetY = currentPoint.Y - _startPoint.Y;

                var newX = _startX + offsetX;
                var newY = _startY + offsetY;

                // Sınırların dışına çıkmayı engelle (Opsiyonel)
                if (newX < 0) newX = 0;
                if (newY < 0) newY = 0;

                Canvas.SetLeft(DraggableChatBox, newX);
                Canvas.SetTop(DraggableChatBox, newY);
            }
        }

        private void ChatBox_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                DraggableChatBox.ReleaseMouseCapture();

                // 1. Yeni konumu kaydet
                var newX = (int)Canvas.GetLeft(DraggableChatBox);
                var newY = (int)Canvas.GetTop(DraggableChatBox);

                var s = SettingsStore.Load();
                s.OverlayX = newX;
                s.OverlayY = newY;
                SettingsStore.Save(s);

                // 2. Canlı Yayındaki Overlay'i güncelle (Erişim biraz dolaylı ama etkili)
                // MainWindow üzerinden Overlay Controller'a ulaşıyoruz.
                if (System.Windows.Application.Current.MainWindow is MainWindow mw)
                {
                    mw.UpdateOverlayPosition(newX, newY);
                }
            }
        }
    }
}