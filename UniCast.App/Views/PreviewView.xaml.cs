using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32; // OpenFileDialog için
using UniCast.App.Services;
using UniCast.Core.Models;
using UniCast.Core.Settings;
using Application = System.Windows.Application;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Point = System.Windows.Point;

namespace UniCast.App.Views
{
    public partial class PreviewView : System.Windows.Controls.UserControl
    {
        // Sahne Listesi (Binding için)
        public ObservableCollection<OverlayItem> SceneItems { get; private set; } = new();

        // Sürükleme Durumu
        private bool _isDragging = false;
        private bool _isResizing = false;
        private Point _startPoint;
        private OverlayItem? _selectedItem; // O an düzenlenen öğe

        public PreviewView(object? viewModel = null)
        {
            InitializeComponent();
            if (viewModel != null) DataContext = viewModel;

            // Öğeleri yükle
            LoadItems();

            // ItemsControl'e kaynağı bağla
            EditorItemsControl.ItemsSource = SceneItems;
        }

        private void LoadItems()
        {
            var s = SettingsStore.Load();
            s.Normalize(); // Null listeleri oluşturur, varsayılan Chat'i ekler

            SceneItems.Clear();
            foreach (var item in s.SceneItems) SceneItems.Add(item);
        }

        private void SaveSettings()
        {
            var s = SettingsStore.Load();
            s.SceneItems = SceneItems.ToList();
            SettingsStore.Save(s);

            // Ana pencereyi uyar (Canlı yayındaki Overlay'i güncelle)
            if (Application.Current.MainWindow is MainWindow mw)
                mw.RefreshOverlay();
        }

        // --- BUTON OLAYLARI ---

        private void AddImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Resimler|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
            if (dlg.ShowDialog() == true)
            {
                var item = new OverlayItem
                {
                    Type = OverlayType.Image,
                    Source = dlg.FileName,
                    X = 50,
                    Y = 50,
                    Width = 200,
                    Height = 200,
                    IsVisible = true
                };
                SceneItems.Add(item);
                SaveSettings();
            }
        }

        private void AddVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Videolar|*.mp4;*.avi;*.mov;*.mkv" };
            if (dlg.ShowDialog() == true)
            {
                var item = new OverlayItem
                {
                    Type = OverlayType.Video,
                    Source = dlg.FileName,
                    X = 50,
                    Y = 50,
                    Width = 300,
                    Height = 170,
                    IsVisible = true
                };
                SceneItems.Add(item);
                SaveSettings();
            }
        }

        private void RemoveItem_Click(object sender, RoutedEventArgs e)
        {
            // Son ekleneni veya seçili olanı sil (Basitlik için sonuncuyu siliyoruz)
            // Gelişmiş versiyonda _selectedItem silinir.
            var itemToRemove = SceneItems.LastOrDefault(x => x.Type != OverlayType.Chat); // Chat silinmesin
            if (itemToRemove != null)
            {
                SceneItems.Remove(itemToRemove);
                SaveSettings();
            }
        }

        // --- SÜRÜKLEME MANTIĞI (Generic) ---

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

                _startPoint = current; // Yeni referans noktası
            }
        }

        private void Item_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                (sender as FrameworkElement)?.ReleaseMouseCapture();
                SaveSettings(); // Konum değişti, kaydet
            }
        }

        // --- BOYUTLANDIRMA MANTIĞI ---

        private void Resize_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is OverlayItem item)
            {
                _selectedItem = item;
                _isResizing = true;
                _startPoint = e.GetPosition(EditorItemsControl);
                e.Handled = true; // Sürüklemeyi engelle
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

                _selectedItem.Width = Math.Max(50, _selectedItem.Width + diffX);
                _selectedItem.Height = Math.Max(50, _selectedItem.Height + diffY);

                _startPoint = current;
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