using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using UniCast.App.Services;
using UniCast.Core.Models;
using UniCast.Core.Settings;
using Application = System.Windows.Application;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace UniCast.App.Views
{
    public partial class PreviewView : UserControl
    {
        public ObservableCollection<OverlayItem> SceneItems { get; private set; } = new();

        // Durum Değişkenleri
        private bool _isDragging = false;
        private bool _isResizing = false;
        private Point _startPoint;
        private OverlayItem? _selectedItem;

        // Resize Değişkenleri (HATA DÜZELTME: Eksik tanımlar eklendi)
        private double _startWidth;
        private double _startHeight;

        public PreviewView(object? viewModel = null)
        {
            InitializeComponent();
            if (viewModel != null) DataContext = viewModel;

            LoadItems();

            // XAML'daki ItemsControl ismine bağlıyoruz
            EditorItemsControl.ItemsSource = SceneItems;
        }

        private void LoadItems()
        {
            var s = SettingsStore.Load();
            s.Normalize();
            SceneItems.Clear();
            foreach (var item in s.SceneItems) SceneItems.Add(item);
        }

        private void SaveSettings()
        {
            var s = SettingsStore.Load();
            s.SceneItems = SceneItems.ToList();
            SettingsStore.Save(s);

            if (Application.Current.MainWindow is MainWindow mw)
                mw.RefreshOverlay();
        }

        // --- BUTONLAR ---
        private void AddImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Resimler|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
            if (dlg.ShowDialog() == true)
            {
                SceneItems.Add(new OverlayItem { Type = OverlayType.Image, Source = dlg.FileName, Width = 200, Height = 200 });
                SaveSettings();
            }
        }

        private void AddVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Videolar|*.mp4;*.avi;*.mov" };
            if (dlg.ShowDialog() == true)
            {
                SceneItems.Add(new OverlayItem { Type = OverlayType.Video, Source = dlg.FileName, Width = 300, Height = 170 });
                SaveSettings();
            }
        }

        private void RemoveItem_Click(object sender, RoutedEventArgs e)
        {
            var item = SceneItems.LastOrDefault(x => x.Type != OverlayType.Chat);
            if (item != null) { SceneItems.Remove(item); SaveSettings(); }
        }

        // --- SÜRÜKLEME (DRAG) ---
        private void Item_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is OverlayItem item)
            {
                _selectedItem = item;
                _isDragging = true;
                // HATA DÜZELTME: OverlayCanvas yerine EditorItemsControl referans alınıyor
                _startPoint = e.GetPosition(EditorItemsControl);
                fe.CaptureMouse();
            }
        }

        private void Item_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isDragging && _selectedItem != null)
            {
                var current = e.GetPosition(EditorItemsControl);
                var diffX = current.X - _startPoint.X;
                var diffY = current.Y - _startPoint.Y;

                _selectedItem.X += diffX;
                _selectedItem.Y += diffY;

                _startPoint = current;
            }
        }

        private void Item_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                (sender as FrameworkElement)?.ReleaseMouseCapture();
                SaveSettings();
            }
        }

        // --- BOYUTLANDIRMA (RESIZE) ---
        private void Resize_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is OverlayItem item)
            {
                _selectedItem = item;
                _isResizing = true;
                _startPoint = e.GetPosition(EditorItemsControl);

                // HATA DÜZELTME: Başlangıç boyutlarını kaydediyoruz
                _startWidth = item.Width;
                _startHeight = item.Height;

                e.Handled = true;
                fe.CaptureMouse();
            }
        }

        private void Resize_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isResizing && _selectedItem != null)
            {
                var current = e.GetPosition(EditorItemsControl);
                var diffX = current.X - _startPoint.X;
                var diffY = current.Y - _startPoint.Y;

                _selectedItem.Width = Math.Max(50, _startWidth + diffX);
                _selectedItem.Height = Math.Max(50, _startHeight + diffY);
            }
        }

        private void Resize_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isResizing)
            {
                _isResizing = false;
                (sender as FrameworkElement)?.ReleaseMouseCapture();
                SaveSettings();
            }
        }
    }
}