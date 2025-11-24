using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UniCast.Core.Core;

namespace UniCast.Core.Models
{
    /// <summary>
    /// UI’da kullanılan hedef satırı (Targets paneli)
    /// </summary>
    public sealed class TargetItem : INotifyPropertyChanged
    {
        private Platform _platform;
        private string? _url;
        private string? _key;
        private bool _enabled;
        private string? _displayName;

        public Platform Platform
        {
            get => _platform;
            set { if (_platform != value) { _platform = value; OnPropertyChanged(); } }
        }

        /// <summary> Örn: rtmps://a.rtmp.youtube.com/live2 </summary>
        public string? Url
        {
            get => _url;
            set { if (_url != value) { _url = value; OnPropertyChanged(); } }
        }

        /// <summary> Yayın anahtarı </summary>
        public string? Key
        {
            get => _key;
            set { if (_key != value) { _key = value; OnPropertyChanged(); } }
        }

        /// <summary> UI’da etkin/pasif </summary>
        public bool Enabled
        {
            get => _enabled;
            set { if (_enabled != value) { _enabled = value; OnPropertyChanged(); } }
        }

        /// <summary> UI’da görünen ad (opsiyonel) </summary>
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

        // INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }
}
