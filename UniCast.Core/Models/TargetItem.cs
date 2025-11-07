using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UniCast.Core.Models
{
    /// <summary>
    /// Eski VM'lerin beklediği hedef modeli. WPF binding için INotifyPropertyChanged uygulanır.
    /// </summary>
    public sealed class TargetItem : INotifyPropertyChanged
    {
        private string? _name;
        private string? _key;
        private string? _url;
        private StreamPlatform _platform = StreamPlatform.Custom;
        private bool _enabled = true;

        public string? Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        public string? Key
        {
            get => _key;
            set => SetField(ref _key, value);
        }

        /// <summary>rtmp/rtmps kök URL (ör. rtmp://a.rtmp.youtube.com/live2)</summary>
        public string? Url
        {
            get => _url;
            set => SetField(ref _url, value);
        }

        public StreamPlatform Platform
        {
            get => _platform;
            set => SetField(ref _platform, value);
        }

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value!;
            OnPropertyChanged(name);
            return true;
        }
    }
}
