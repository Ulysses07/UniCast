using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input; // MouseButtonEventArgs buradan gelir
using UniCast.App.Services;
using Application = System.Windows.Application;
using Point = System.Windows.Point;

namespace UniCast.App.Views
{
    // HATA DÜZELTME: WPF UserControl
    public partial class PreviewView : System.Windows.Controls.UserControl
    {
        private bool _isDragging = false;
        private Point _startPoint;
        private double _startX, _startY;

        private bool _isResizing = false;
        private Point _resizeStartPoint;
        private double _startWidth;

        public PreviewView(object? viewModel = null)
        {
            InitializeComponent();
            if (viewModel != null) DataContext = viewModel;

            var s = SettingsStore.Load();
            Canvas.SetLeft(DraggableChatBox, s.OverlayX);
            Canvas.SetTop(DraggableChatBox, s.OverlayY);
            DraggableChatBox.Width = s.OverlayWidth;
        }

        // --- TAŞIMA (DRAG) ---
        private void ChatBox_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // HATA DÜZELTME: 'ResizeHandle' ismini kullanıyoruz
            if (e.OriginalSource == ResizeHandle) return;

            _isDragging = true;
            _startPoint = e.GetPosition(OverlayCanvas);
            _startX = Canvas.GetLeft(DraggableChatBox);
            _startY = Canvas.GetTop(DraggableChatBox);
            DraggableChatBox.CaptureMouse();
        }

        // HATA DÜZELTME: System.Windows.Input.MouseEventArgs (Tam Ad)
        private void ChatBox_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isDragging)
            {
                var current = e.GetPosition(OverlayCanvas);
                var offX = current.X - _startPoint.X;
                var offY = current.Y - _startPoint.Y;

                var newX = Math.Max(0, _startX + offX);
                var newY = Math.Max(0, _startY + offY);

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
                SavePosition();
            }
        }

        // --- BOYUTLANDIRMA (RESIZE) ---
        private void Resize_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isResizing = true;
            _resizeStartPoint = e.GetPosition(OverlayCanvas);
            _startWidth = DraggableChatBox.Width;

            e.Handled = true;
            // HATA DÜZELTME: 'ResizeHandle' ismini kullanıyoruz
            ResizeHandle.CaptureMouse();
        }

        // HATA DÜZELTME: System.Windows.Input.MouseEventArgs (Tam Ad)
        private void Resize_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isResizing)
            {
                var current = e.GetPosition(OverlayCanvas);
                var diffX = current.X - _resizeStartPoint.X;

                // Min genişlik 200px
                DraggableChatBox.Width = Math.Max(200, _startWidth + diffX);
            }
        }

        private void Resize_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isResizing)
            {
                _isResizing = false;
                // HATA DÜZELTME: 'ResizeHandle' ismini kullanıyoruz
                ResizeHandle.ReleaseMouseCapture();
                SaveSize();
            }
        }

        // --- KAYIT ---
        private void SavePosition()
        {
            var s = SettingsStore.Load();
            s.OverlayX = (int)Canvas.GetLeft(DraggableChatBox);
            s.OverlayY = (int)Canvas.GetTop(DraggableChatBox);
            SettingsStore.Save(s);

            if (Application.Current.MainWindow is MainWindow mw)
                mw.UpdateOverlayPosition(s.OverlayX, s.OverlayY);
        }

        private void SaveSize()
        {
            var s = SettingsStore.Load();
            s.OverlayWidth = DraggableChatBox.Width;
            SettingsStore.Save(s);

            // HATA DÜZELTME: MainWindow.cs içine UpdateOverlaySize metodunu ekleyeceğiz
            if (Application.Current.MainWindow is MainWindow mw)
                mw.UpdateOverlaySize(s.OverlayWidth);
        }
    }
}