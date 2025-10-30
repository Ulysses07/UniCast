using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using UniCast.App.Infrastructure;
using UniCast.App.Services;
using UniCast.Core.Settings;

namespace UniCast.App.ViewModels
{
    public sealed class SettingsViewModel : INotifyPropertyChanged
    {
        private readonly IDeviceService _devices;
        private SettingsData _settings;

        public SettingsViewModel(IDeviceService devices)
        {
            _devices = devices;
            _settings = SettingsStore.Load();

            _defaultCamera = _settings.DefaultCamera ?? string.Empty;
            _defaultMicrophone = _settings.DefaultMicrophone ?? string.Empty;
            _encoder = string.IsNullOrWhiteSpace(_settings.Encoder) ? "auto" : _settings.Encoder!;
            _videoKbps = _settings.VideoKbps;
            _audioKbps = _settings.AudioKbps;
            _fps = _settings.Fps;
            _width = _settings.Width;
            _height = _settings.Height;
            _recordFolder = _settings.RecordFolder ?? string.Empty;
            _enableLocalRecord = _settings.EnableLocalRecord;

            SaveCommand = new RelayCommand(_ => Save());
            BrowseRecordFolderCommand = new RelayCommand(_ => BrowseFolder());
            RefreshDevicesCommand = new RelayCommand(_ => RefreshDevices());

            RefreshDevices();

            if (string.IsNullOrWhiteSpace(_defaultCamera) && VideoDevices.Count > 0)
                DefaultCamera = VideoDevices[0];
            if (string.IsNullOrWhiteSpace(_defaultMicrophone) && AudioDevices.Count > 0)
                DefaultMicrophone = AudioDevices[0];
        }

        public ObservableCollection<string> VideoDevices { get; } = new();
        public ObservableCollection<string> AudioDevices { get; } = new();

        private string _defaultCamera = string.Empty;
        public string DefaultCamera
        {
            get => _defaultCamera ?? string.Empty;
            set { _defaultCamera = value ?? string.Empty; OnPropertyChanged(); }
        }

        private string _defaultMicrophone = string.Empty;
        public string DefaultMicrophone
        {
            get => _defaultMicrophone ?? string.Empty;
            set { _defaultMicrophone = value ?? string.Empty; OnPropertyChanged(); }
        }

        private string _encoder = "auto";
        public string Encoder
        {
            get => string.IsNullOrWhiteSpace(_encoder) ? "auto" : _encoder;
            set { _encoder = string.IsNullOrWhiteSpace(value) ? "auto" : value; OnPropertyChanged(); }
        }

        private int _videoKbps = 3500;
        public int VideoKbps { get => _videoKbps; set { _videoKbps = value; OnPropertyChanged(); } }

        private int _audioKbps = 160;
        public int AudioKbps { get => _audioKbps; set { _audioKbps = value; OnPropertyChanged(); } }

        private int _fps = 30;
        public int Fps { get => _fps; set { _fps = value; OnPropertyChanged(); } }

        private int _width = 1280;
        public int Width { get => _width; set { _width = value; OnPropertyChanged(); } }

        private int _height = 720;
        public int Height { get => _height; set { _height = value; OnPropertyChanged(); } }

        private string _recordFolder = string.Empty;
        public string RecordFolder { get => _recordFolder ?? string.Empty; set { _recordFolder = value ?? string.Empty; OnPropertyChanged(); } }

        private bool _enableLocalRecord;
        public bool EnableLocalRecord { get => _enableLocalRecord; set { _enableLocalRecord = value; OnPropertyChanged(); } }

        public ICommand SaveCommand { get; }
        public ICommand BrowseRecordFolderCommand { get; }
        public ICommand RefreshDevicesCommand { get; }

        private void Save()
        {
            _settings.DefaultCamera = DefaultCamera.Trim();
            _settings.DefaultMicrophone = DefaultMicrophone.Trim();
            _settings.Encoder = string.IsNullOrWhiteSpace(Encoder) ? "auto" : Encoder.Trim();
            _settings.VideoKbps = VideoKbps;
            _settings.AudioKbps = AudioKbps;
            _settings.Fps = Fps;
            _settings.Width = Width;
            _settings.Height = Height;
            _settings.RecordFolder = RecordFolder.Trim();
            _settings.EnableLocalRecord = EnableLocalRecord;

            SettingsStore.Save(_settings);
        }

        private void BrowseFolder()
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Kayıt klasörünü seç",
                ShowNewFolderButton = true
            };
            var result = dlg.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            {
                RecordFolder = dlg.SelectedPath;
            }
        }

        private void RefreshDevices()
        {
            var (video, audio) = _devices.ListDevices();

            VideoDevices.Clear();
            foreach (var v in video) VideoDevices.Add(v);
            if (!string.IsNullOrWhiteSpace(DefaultCamera) && !VideoDevices.Contains(DefaultCamera))
                VideoDevices.Add(DefaultCamera);

            AudioDevices.Clear();
            foreach (var a in audio) AudioDevices.Add(a);
            if (!string.IsNullOrWhiteSpace(DefaultMicrophone) && !AudioDevices.Contains(DefaultMicrophone))
                AudioDevices.Add(DefaultMicrophone);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
