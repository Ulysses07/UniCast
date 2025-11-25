using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UniCast.Core.Streaming; // ÖNEMLİ: Platform enum'ı buradan gelmeli

namespace UniCast.Core.Models
{
    /// <summary>
    /// UI’da kullanılan hedef satırı (Targets paneli)
    /// </summary>
    public sealed class TargetItem : INotifyPropertyChanged
    {
        // HATA DÜZELTME 1: Eksik olan 'Id' özelliği eklendi
        public string Id { get; set; } = Guid.NewGuid().ToString();

        // HATA DÜZELTME 2: Tür 'StreamPlatform' yapıldı (Platform çakışmasını önler)
        private StreamPlatform _platform;
        public StreamPlatform Platform
        {
            get => _platform;
            set { if (_platform != value) { _platform = value; OnPropertyChanged(); } }
        }

        private string? _url;
        public string? Url
        {
            get => _url;
            set { if (_url != value) { _url = value; OnPropertyChanged(); } }
        }

        // HATA DÜZELTME 3: 'Key' ismi 'StreamKey' olarak değiştirildi (ViewModel ile uyum için)
        private string? _streamKey;
        public string? StreamKey
        {
            get => _streamKey;
            set { if (_streamKey != value) { _streamKey = value; OnPropertyChanged(); } }
        }

        private bool _enabled;
        public bool Enabled
        {
            get => _enabled;
            set { if (_enabled != value) { _enabled = value; OnPropertyChanged(); } }
        }

        private string? _displayName;
        public string? DisplayName
        {
            get => _displayName;
            set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
        }

        public override string ToString()
        {
            var name = !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : Platform.ToString();
            return $"{name} | {(Enabled ? "On" : "Off")} | {Url ?? "-"}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }
}