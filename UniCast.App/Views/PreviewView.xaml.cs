using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using UniCast.App.Services;
using UniCast.App.ViewModels;
using UniCast.Core.Models;
using UniCast.Core.Settings;
using Application = System.Windows.Application;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace UniCast.App.Views
{
    /// <summary>
    /// Preview View - Sahne editörü.
    /// DÜZELTME: MVVM uyumlu hale getirildi, SceneItems ViewModel'den yönetiliyor.
    /// </summary>
    public partial class PreviewView : UserControl
    {
        private readonly PreviewViewModel? _viewModel;

        // DÜZELTME: SceneItems artık yerel bir ObservableCollection
        // ViewModel'den bağımsız, sadece sahne editörü için
        private readonly ObservableCollection<OverlayItem> _sceneItems = new();

        // Durum Değişkenleri
        private bool _isDragging = false;
        private bool _isResizing = false;
        private Point _startPoint;
        private OverlayItem? _selectedItem;

        // Resize Değişkenleri
        private double _startWidth;
        private double _startHeight;

        public PreviewView(object? viewModel = null)
        {
            InitializeComponent();

            _viewModel = viewModel as PreviewViewModel;

            // DÜZELTME: Composite DataContext - hem preview hem scene items için
            DataContext = new PreviewViewDataContext
            {
                PreviewViewModel = _viewModel,
                SceneItems = _sceneItems
            };

            LoadItems();
            EditorItemsControl.ItemsSource = _sceneItems;
        }

        private void LoadItems()
        {
            var s = SettingsStore.Load();
            s.Normalize();
            _sceneItems.Clear();
            foreach (var item in s.SceneItems)
                _sceneItems.Add(item);
        }

        private void SaveSettings()
        {
            var s = SettingsStore.Load();
            s.SceneItems = _sceneItems.ToList();
            SettingsStore.Save(s);

            if (Application.Current.MainWindow is MainWindow mw)
                mw.RefreshOverlay();
        }

        #region Butonlar

        private void AddImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Resimler|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
            if (dlg.ShowDialog() == true)
            {
                _sceneItems.Add(new OverlayItem
                {
                    Type = OverlayType.Image,
                    Source = dlg.FileName,
                    Width = 200,
                    Height = 200
                });
                SaveSettings();
            }
        }

        private void AddVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Videolar|*.mp4;*.avi;*.mov" };
            if (dlg.ShowDialog() == true)
            {
                _sceneItems.Add(new OverlayItem
                {
                    Type = OverlayType.Video,
                    Source = dlg.FileName,
                    Width = 300,
                    Height = 170
                });
                SaveSettings();
            }
        }

        private void RemoveItem_Click(object sender, RoutedEventArgs e)
        {
            var item = _sceneItems.LastOrDefault(x => x.Type != OverlayType.Chat);
            if (item != null)
            {
                _sceneItems.Remove(item);
                SaveSettings();
            }
        }

        #endregion

        #region Sürükleme (Drag)

        private void Item_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is OverlayItem item)
            {
                _selectedItem = item;
                _isDragging = true;
                _startPoint = e.GetPosition(EditorItemsControl);
                fe.CaptureMouse();
            }
        }

        private void Item_MouseMove(object sender, MouseEventArgs e)
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

        #endregion

        #region Boyutlandırma (Resize)

        private void Resize_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is OverlayItem item)
            {
                _selectedItem = item;
                _isResizing = true;
                _startPoint = e.GetPosition(EditorItemsControl);
                _startWidth = item.Width;
                _startHeight = item.Height;

                e.Handled = true;
                fe.CaptureMouse();
            }
        }

        private void Resize_MouseMove(object sender, MouseEventArgs e)
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

        #endregion
    }

    /// <summary>
    /// DÜZELTME: PreviewView için composite DataContext.
    /// Hem PreviewViewModel hem de SceneItems'ı içerir.
    /// </summary>
    public class PreviewViewDataContext
    {
        public PreviewViewModel? PreviewViewModel { get; set; }
        public ObservableCollection<OverlayItem>? SceneItems { get; set; }
    }
}