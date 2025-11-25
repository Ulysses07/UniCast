using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using UniCast.App.Infrastructure;
using UniCast.App.Services;
using UniCast.App.Services.Capture;
using UniCast.Core.Settings;
using UniCast.Core.Models;

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

            // Ayarları Yükle
            _defaultCamera = _settings.DefaultCamera ?? "";
            _defaultMicrophone = _settings.DefaultMicrophone ?? "";
            _encoder = string.IsNullOrWhiteSpace(_settings.Encoder) ? "auto" : _settings.Encoder!;
            _videoKbps = _settings.VideoKbps;
            _audioKbps = _settings.AudioKbps;
            _audioDelayMs = _settings.AudioDelayMs;
            _fps = _settings.Fps;
            _width = _settings.Width;
            _height = _settings.Height;
            _recordFolder = _settings.RecordFolder ?? "";
            _enableLocalRecord = _settings.EnableLocalRecord;

            // Sosyal Medya
            _instagramUserId = _settings.InstagramUserId ?? "";
            _instagramSessionId = _settings.InstagramSessionId ?? "";
            _facebookPageId = _settings.FacebookPageId ?? "";
            _facebookLiveVideoId = _settings.FacebookLiveVideoId ?? "";
            _facebookAccessToken = _settings.FacebookAccessToken ?? "";

            // Komutlar
            SaveCommand = new RelayCommand(_ => Save());
            BrowseRecordFolderCommand = new RelayCommand(_ => BrowseFolder());
            RefreshDevicesCommand = new RelayCommand(async _ => await RefreshDevicesAsync());

            _ = RefreshDevicesAsync();
        }

        public ObservableCollection<CaptureDevice> VideoDevices { get; } = new();
        public ObservableCollection<CaptureDevice> AudioDevices { get; } = new();

        // Alanlar
        private string _defaultCamera;
        public string DefaultCamera
        {
            get => _defaultCamera;
            set { _defaultCamera = value; _settings.SelectedVideoDevice = value; OnPropertyChanged(); }
        }

        private string _defaultMicrophone;
        public string DefaultMicrophone
        {
            get => _defaultMicrophone;
            set { _defaultMicrophone = value; _settings.SelectedAudioDevice = value; OnPropertyChanged(); }
        }

        private string _encoder;
        public string Encoder { get => _encoder; set { _encoder = value; OnPropertyChanged(); } }

        private int _videoKbps;
        public int VideoKbps { get => _videoKbps; set { _videoKbps = value; OnPropertyChanged(); } }

        private int _audioKbps;
        public int AudioKbps { get => _audioKbps; set { _audioKbps = value; OnPropertyChanged(); } }

        private int _audioDelayMs;
        public int AudioDelayMs { get => _audioDelayMs; set { _audioDelayMs = value; OnPropertyChanged(); } }

        private int _fps;
        public int Fps { get => _fps; set { _fps = value; OnPropertyChanged(); } }

        private int _width;
        public int Width { get => _width; set { _width = value; OnPropertyChanged(); } }

        private int _height;
        public int Height { get => _height; set { _height = value; OnPropertyChanged(); } }

        private string _recordFolder;
        public string RecordFolder { get => _recordFolder; set { _recordFolder = value; OnPropertyChanged(); } }

        private bool _enableLocalRecord;
        public bool EnableLocalRecord { get => _enableLocalRecord; set { _enableLocalRecord = value; OnPropertyChanged(); } }

        // Sosyal Alanlar
        private string _instagramUserId;
        public string InstagramUserId { get => _instagramUserId; set { _instagramUserId = value; OnPropertyChanged(); } }

        private string _instagramSessionId;
        public string InstagramSessionId { get => _instagramSessionId; set { _instagramSessionId = value; OnPropertyChanged(); } }

        private string _facebookPageId;
        public string FacebookPageId { get => _facebookPageId; set { _facebookPageId = value; OnPropertyChanged(); } }

        private string _facebookLiveVideoId;
        public string FacebookLiveVideoId { get => _facebookLiveVideoId; set { _facebookLiveVideoId = value; OnPropertyChanged(); } }

        private string _facebookAccessToken;
        public string FacebookAccessToken { get => _facebookAccessToken; set { _facebookAccessToken = value; OnPropertyChanged(); } }

        public ICommand SaveCommand { get; }
        public ICommand BrowseRecordFolderCommand { get; }
        public ICommand RefreshDevicesCommand { get; }

        private void Save()
        {
            _settings.DefaultCamera = (DefaultCamera ?? "").Trim();
            _settings.DefaultMicrophone = (DefaultMicrophone ?? "").Trim();
            _settings.SelectedVideoDevice = _settings.DefaultCamera;
            _settings.SelectedAudioDevice = _settings.DefaultMicrophone;

            _settings.Encoder = string.IsNullOrWhiteSpace(Encoder) ? "auto" : Encoder.Trim();
            _settings.VideoKbps = VideoKbps;
            _settings.AudioKbps = AudioKbps;
            _settings.AudioDelayMs = AudioDelayMs;
            _settings.Fps = Fps;
            _settings.Width = Width;
            _settings.Height = Height;
            _settings.RecordFolder = (RecordFolder ?? "").Trim();
            _settings.EnableLocalRecord = EnableLocalRecord;

            _settings.InstagramUserId = (InstagramUserId ?? "").Trim();
            _settings.InstagramSessionId = (InstagramSessionId ?? "").Trim();
            _settings.FacebookPageId = (FacebookPageId ?? "").Trim();
            _settings.FacebookLiveVideoId = (FacebookLiveVideoId ?? "").Trim();
            _settings.FacebookAccessToken = (FacebookAccessToken ?? "").Trim();

            SettingsStore.Save(_settings);
        }

        private void BrowseFolder()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                CheckFileExists = false,
                FileName = "Klasör seç",
                ValidateNames = false
            };
            if (dlg.ShowDialog() == true)
            {
                var path = System.IO.Path.GetDirectoryName(dlg.FileName) ?? "";
                if (!string.IsNullOrWhiteSpace(path))
                    RecordFolder = path;
            }
        }

        private async Task RefreshDevicesAsync()
        {
            var videos = await _devices.GetVideoDevicesAsync();
            var audios = await _devices.GetAudioDevicesAsync();

            VideoDevices.Clear();
            foreach (var v in videos) VideoDevices.Add(v);

            AudioDevices.Clear();
            foreach (var a in audios) AudioDevices.Add(a);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}