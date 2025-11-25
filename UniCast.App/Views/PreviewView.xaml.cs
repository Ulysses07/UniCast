using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniCast.App.Services;

namespace UniCast.App.Views
{
    // HATA DÜZELTME: WPF UserControl olduğunu belirtiyoruz
    public partial class PreviewView : System.Windows.Controls.UserControl
    {
        private bool _isDragging = false;
        private System.Windows.Point _startPoint; // System.Windows.Point (Using ekli olduğu için sorun yok ama aşağıda dikkat)
        private double _startX, _startY;

        public PreviewView(object? viewModel = null)
        {
            InitializeComponent();
            if (viewModel != null) DataContext = viewModel;

            var s = SettingsStore.Load();
            Canvas.SetLeft(DraggableChatBox, s.OverlayX);
            Canvas.SetTop(DraggableChatBox, s.OverlayY);
        }

        // HATA DÜZELTME: MouseButtonEventArgs zaten WPF'e özeldir ama MouseEventArgs çakışabilir.
        // System.Windows.Input namespace'i yukarıda olduğu için genelde sorun olmaz ama
        // garanti olsun diye parametreleri kontrol ediyoruz.
        private void ChatBox_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            _startPoint = e.GetPosition(OverlayCanvas);
            _startX = Canvas.GetLeft(DraggableChatBox);
            _startY = Canvas.GetTop(DraggableChatBox);
            DraggableChatBox.CaptureMouse();
        }

        // HATA DÜZELTME: MouseEventArgs (WPF: System.Windows.Input)
        private void ChatBox_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isDragging)
            {
                var currentPoint = e.GetPosition(OverlayCanvas);
                var offsetX = currentPoint.X - _startPoint.X;
                var offsetY = currentPoint.Y - _startPoint.Y;

                var newX = _startX + offsetX;
                var newY = _startY + offsetY;

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

                var newX = (int)Canvas.GetLeft(DraggableChatBox);
                var newY = (int)Canvas.GetTop(DraggableChatBox);

                var s = SettingsStore.Load();
                s.OverlayX = newX;
                s.OverlayY = newY;
                SettingsStore.Save(s);

                if (System.Windows.Application.Current.MainWindow is MainWindow mw)
                {
                    mw.UpdateOverlayPosition(newX, newY);
                }
            }
        }
    }
}