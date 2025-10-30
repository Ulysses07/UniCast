using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UniCast.Core.Models
{
    public sealed class TargetItem : INotifyPropertyChanged
    {
        private string _url = string.Empty;
        private bool _enabled = true;

        public string Url
        {
            get => _url;
            set { _url = value; OnPropertyChanged(); }
        }

        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
