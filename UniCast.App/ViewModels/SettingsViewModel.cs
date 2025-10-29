using System.ComponentModel;
using System.Runtime.CompilerServices;
using UniCast.App.Services;

namespace UniCast.App.ViewModels
{
    /// <summary>
    /// Uygulama ayarlarını UI ve SettingsStore arasında senkronize eder.
    /// </summary>
    public sealed class SettingsViewModel : INotifyPropertyChanged
    {
        private SettingsData _settings;

        private int _defaultVideoKbps;
        private int _defaultFps;
        private string _recordFolder = "";

        public SettingsViewModel()
        {
            _settings = SettingsStore.Load();
            _defaultVideoKbps = _settings.VideoKbps;
            _defaultFps = _settings.Fps;
            _recordFolder = _settings.RecordFolder;
        }

        public int DefaultVideoKbps
        {
            get => _defaultVideoKbps;
            set
            {
                if (_defaultVideoKbps != value)
                {
                    _defaultVideoKbps = value;
                    OnPropertyChanged();
                }
            }
        }

        public int DefaultFps
        {
            get => _defaultFps;
            set
            {
                if (_defaultFps != value)
                {
                    _defaultFps = value;
                    OnPropertyChanged();
                }
            }
        }

        public string RecordFolder
        {
            get => _recordFolder;
            set
            {
                if (_recordFolder != value)
                {
                    _recordFolder = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Ayarları diske kaydeder.
        /// </summary>
        public void Save()
        {
            _settings.VideoKbps = DefaultVideoKbps;
            _settings.Fps = DefaultFps;
            _settings.RecordFolder = RecordFolder;

            SettingsStore.Save(_settings);
        }

        public void Reload()
        {
            _settings = SettingsStore.Load();
            DefaultVideoKbps = _settings.VideoKbps;
            DefaultFps = _settings.Fps;
            RecordFolder = _settings.RecordFolder;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
