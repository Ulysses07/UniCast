using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UniCast.Core.Models
{
    public enum OverlayType
    {
        Chat,       // Sohbet Akışı
        Image,      // Logo, Resim, QR Kod (Önceden kaydedilmiş resim olarak)
        Video,      // Tanıtım Videosu (Loop)
        Text        // Sabit Yazı (Örn: "İndirim Kodu: YAZ50")
    }

    public sealed class OverlayItem : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public OverlayType Type { get; set; } = OverlayType.Image;

        // İçerik Yolu (Resim dosyası yolu, Yazı içeriği vb.)
        private string _source = "";
        public string Source
        {
            get => _source;
            set { if (_source != value) { _source = value; OnPropertyChanged(); } }
        }

        // Konum ve Boyut
        private double _x = 20;
        private double _y = 20;
        private double _width = 200;
        private double _height = 150;
        private double _opacity = 1.0;
        private bool _isVisible = true;

        public double X
        {
            get => _x;
            set { if (_x != value) { _x = value; OnPropertyChanged(); } }
        }
        public double Y
        {
            get => _y;
            set { if (_y != value) { _y = value; OnPropertyChanged(); } }
        }
        public double Width
        {
            get => _width;
            set { if (_width != value) { _width = value; OnPropertyChanged(); } }
        }
        public double Height
        {
            get => _height;
            set { if (_height != value) { _height = value; OnPropertyChanged(); } }
        }
        public double Opacity
        {
            get => _opacity;
            set { if (_opacity != value) { _opacity = value; OnPropertyChanged(); } }
        }
        public bool IsVisible
        {
            get => _isVisible;
            set { if (_isVisible != value) { _isVisible = value; OnPropertyChanged(); } }
        }

        // UI seçimi için (Editörde hangisi seçili?)
        private bool _isSelected;
        [System.Text.Json.Serialization.JsonIgnore] // Kaydederken bunu yoksay
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}