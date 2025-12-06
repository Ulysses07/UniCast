using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using UniCast.App.Infrastructure;
using UniCast.App.Services;
using UniCast.App.Services.Capture;
using UniCast.Core.Models;

// App.Services.SettingsData kullan
using SettingsData = UniCast.App.Services.SettingsData;

namespace UniCast.App.ViewModels
{
    /// <summary>
    /// DÜZELTME v18: Ayarlar kaydedildi onayı ve validation eklendi.
    /// </summary>
    public sealed class SettingsViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly IDeviceService? _devices;
        private SettingsData _settings;
        private bool _disposed;

        public SettingsViewModel() : this(new DeviceService())
        {
        }

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

            // YouTube
            _youTubeApiKey = _settings.YouTubeApiKey ?? "";
            _youTubeVideoId = _settings.YouTubeVideoId ?? "";

            // Twitch
            _twitchChannelName = _settings.TwitchChannelName ?? "";
            _twitchOAuthToken = _settings.TwitchOAuthToken ?? "";
            _twitchBotUsername = _settings.TwitchBotUsername ?? "";

            // Sosyal Medya
            _instagramUserId = _settings.InstagramUsername ?? "";
            _instagramSessionId = _settings.InstagramPassword ?? "";
            _facebookPageId = _settings.FacebookPageId ?? "";
            _facebookLiveVideoId = _settings.FacebookLiveVideoId ?? "";
            _facebookAccessToken = _settings.FacebookPageAccessToken ?? "";

            // Yeni WebView2 tabanlı Facebook
            _facebookCookies = _settings.FacebookCookies ?? "";
            _facebookUserId = _settings.FacebookUserId ?? "";
            _facebookLiveVideoUrl = _settings.FacebookLiveVideoUrl ?? "";

            // Komutlar
            SaveCommand = new RelayCommand(_ => Save());
            BrowseRecordFolderCommand = new RelayCommand(_ => BrowseFolder());
            RefreshDevicesCommand = new RelayCommand(async _ => await RefreshDevicesAsync());

            _ = RefreshDevicesAsync();
        }

        public ObservableCollection<CaptureDevice> VideoDevices { get; } = new();
        public ObservableCollection<CaptureDevice> AudioDevices { get; } = new();

        #region DÜZELTME v18: Save Status Properties

        private bool _isSaving;
        /// <summary>
        /// Kaydetme işlemi devam ediyor mu
        /// </summary>
        public bool IsSaving
        {
            get => _isSaving;
            private set { _isSaving = value; OnPropertyChanged(); }
        }

        private bool _saveSuccess;
        /// <summary>
        /// Son kaydetme başarılı mı
        /// </summary>
        public bool SaveSuccess
        {
            get => _saveSuccess;
            private set { _saveSuccess = value; OnPropertyChanged(); }
        }

        private string? _saveMessage;
        /// <summary>
        /// Kaydetme durumu mesajı
        /// </summary>
        public string? SaveMessage
        {
            get => _saveMessage;
            private set { _saveMessage = value; OnPropertyChanged(); }
        }

        private bool _hasUnsavedChanges;
        /// <summary>
        /// Kaydedilmemiş değişiklikler var mı
        /// </summary>
        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            private set { _hasUnsavedChanges = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Ayarlar kaydedildiğinde tetiklenen event
        /// </summary>
        public event EventHandler<SettingsSavedEventArgs>? OnSettingsSaved;

        #endregion

        // DÜZELTME: DefaultCamera - hem DefaultCamera hem SelectedVideoDevice güncelleniyor
        private string _defaultCamera;
        public string DefaultCamera
        {
            get => _defaultCamera;
            set
            {
                _defaultCamera = value;
                _settings.DefaultCamera = value;
                _settings.SelectedVideoDevice = value;
                HasUnsavedChanges = true;
                OnPropertyChanged();
            }
        }

        // DÜZELTME: DefaultMicrophone - hem DefaultMicrophone hem SelectedAudioDevice güncelleniyor
        private string _defaultMicrophone;
        public string DefaultMicrophone
        {
            get => _defaultMicrophone;
            set
            {
                _defaultMicrophone = value;
                _settings.DefaultMicrophone = value;
                _settings.SelectedAudioDevice = value;
                HasUnsavedChanges = true;
                OnPropertyChanged();
            }
        }

        private string _encoder;
        public string Encoder
        {
            get => _encoder;
            set { _encoder = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private int _videoKbps;
        public int VideoKbps
        {
            get => _videoKbps;
            set { _videoKbps = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private int _audioKbps;
        public int AudioKbps
        {
            get => _audioKbps;
            set { _audioKbps = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private int _audioDelayMs;
        public int AudioDelayMs
        {
            get => _audioDelayMs;
            set { _audioDelayMs = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private int _fps;
        public int Fps
        {
            get => _fps;
            set { _fps = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private int _width;
        public int Width
        {
            get => _width;
            set { _width = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private int _height;
        public int Height
        {
            get => _height;
            set { _height = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private string _recordFolder;
        public string RecordFolder
        {
            get => _recordFolder;
            set { _recordFolder = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private bool _enableLocalRecord;
        public bool EnableLocalRecord
        {
            get => _enableLocalRecord;
            set { _enableLocalRecord = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        // YouTube
        private string _youTubeApiKey;
        public string YouTubeApiKey
        {
            get => _youTubeApiKey;
            set { _youTubeApiKey = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private string _youTubeVideoId;
        public string YouTubeVideoId
        {
            get => _youTubeVideoId;
            set { _youTubeVideoId = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        // Twitch
        private string _twitchChannelName;
        public string TwitchChannelName
        {
            get => _twitchChannelName;
            set { _twitchChannelName = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private string _twitchOAuthToken;
        public string TwitchOAuthToken
        {
            get => _twitchOAuthToken;
            set { _twitchOAuthToken = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private string _twitchBotUsername;
        public string TwitchBotUsername
        {
            get => _twitchBotUsername;
            set { _twitchBotUsername = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        // Sosyal Alanlar
        private string _instagramUserId;
        public string InstagramUserId
        {
            get => _instagramUserId;
            set { _instagramUserId = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private string _instagramSessionId;
        public string InstagramSessionId
        {
            get => _instagramSessionId;
            set { _instagramSessionId = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private string _facebookPageId;
        public string FacebookPageId
        {
            get => _facebookPageId;
            set { _facebookPageId = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private string _facebookLiveVideoId;
        public string FacebookLiveVideoId
        {
            get => _facebookLiveVideoId;
            set { _facebookLiveVideoId = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private string _facebookAccessToken;
        public string FacebookAccessToken
        {
            get => _facebookAccessToken;
            set { _facebookAccessToken = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        // Yeni WebView2 tabanlı Facebook ayarları
        private string _facebookCookies = "";
        public string FacebookCookies
        {
            get => _facebookCookies;
            set { _facebookCookies = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private string _facebookUserId = "";
        public string FacebookUserId
        {
            get => _facebookUserId;
            set { _facebookUserId = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        private string _facebookLiveVideoUrl = "";
        public string FacebookLiveVideoUrl
        {
            get => _facebookLiveVideoUrl;
            set { _facebookLiveVideoUrl = value; HasUnsavedChanges = true; OnPropertyChanged(); }
        }

        public ICommand SaveCommand { get; }
        public ICommand BrowseRecordFolderCommand { get; }
        public ICommand RefreshDevicesCommand { get; }

        /// <summary>
        /// DÜZELTME v18: Geliştirilmiş kaydetme metodu - validation ve onay mesajı
        /// </summary>
        private void Save()
        {
            IsSaving = true;
            SaveMessage = null;
            SaveSuccess = false;

            try
            {
                // DÜZELTME v18: Validation
                var validationResult = ValidateSettings();
                if (!validationResult.IsValid)
                {
                    SaveMessage = $"❌ {validationResult.ErrorMessage}";
                    SaveSuccess = false;
                    return;
                }

                // SettingsStore.Data'yı güncelle
                SettingsStore.Update(s =>
                {
                    s.DefaultCamera = (DefaultCamera ?? "").Trim();
                    s.DefaultMicrophone = (DefaultMicrophone ?? "").Trim();
                    s.Encoder = string.IsNullOrWhiteSpace(Encoder) ? "auto" : Encoder.Trim();
                    s.VideoKbps = VideoKbps;
                    s.AudioKbps = AudioKbps;
                    s.AudioDelayMs = AudioDelayMs;
                    s.Fps = Fps;
                    s.Width = Width;
                    s.Height = Height;
                    s.RecordFolder = (RecordFolder ?? "").Trim();
                    s.EnableLocalRecord = EnableLocalRecord;

                    // YouTube
                    s.YouTubeApiKey = (YouTubeApiKey ?? "").Trim();
                    s.YouTubeVideoId = (YouTubeVideoId ?? "").Trim();

                    // Twitch
                    s.TwitchChannelName = (TwitchChannelName ?? "").Trim();
                    s.TwitchOAuthToken = (TwitchOAuthToken ?? "").Trim();
                    s.TwitchBotUsername = (TwitchBotUsername ?? "").Trim();

                    // Sosyal Medya
                    s.InstagramUsername = (InstagramUserId ?? "").Trim();
                    s.InstagramPassword = (InstagramSessionId ?? "").Trim();
                    s.FacebookPageId = (FacebookPageId ?? "").Trim();
                    s.FacebookPageAccessToken = (FacebookAccessToken ?? "").Trim();

                    // Yeni WebView2 tabanlı Facebook
                    s.FacebookCookies = (FacebookCookies ?? "").Trim();
                    s.FacebookUserId = (FacebookUserId ?? "").Trim();
                    s.FacebookLiveVideoUrl = (FacebookLiveVideoUrl ?? "").Trim();
                });

                SettingsStore.Save();

                // DÜZELTME v18: Başarı durumu
                SaveSuccess = true;
                SaveMessage = "✅ Ayarlar başarıyla kaydedildi";
                HasUnsavedChanges = false;

                // Event tetikle
                OnSettingsSaved?.Invoke(this, new SettingsSavedEventArgs
                {
                    Success = true,
                    Message = "Ayarlar kaydedildi",
                    Timestamp = DateTime.Now
                });

                // DÜZELTME v18: 3 saniye sonra mesajı temizle
                _ = ClearSaveMessageAfterDelay();
            }
            catch (Exception ex)
            {
                SaveSuccess = false;
                SaveMessage = $"❌ Kaydetme hatası: {ex.Message}";

                OnSettingsSaved?.Invoke(this, new SettingsSavedEventArgs
                {
                    Success = false,
                    Message = ex.Message,
                    Timestamp = DateTime.Now
                });
            }
            finally
            {
                IsSaving = false;
            }
        }

        /// <summary>
        /// DÜZELTME v18: Ayarları doğrula
        /// </summary>
        private ValidationResult ValidateSettings()
        {
            // Video bitrate kontrolü
            if (VideoKbps < 500 || VideoKbps > 50000)
            {
                return new ValidationResult(false, "Video bitrate 500-50000 kbps arasında olmalı");
            }

            // Audio bitrate kontrolü
            if (AudioKbps < 32 || AudioKbps > 320)
            {
                return new ValidationResult(false, "Audio bitrate 32-320 kbps arasında olmalı");
            }

            // FPS kontrolü
            if (Fps < 15 || Fps > 60)
            {
                return new ValidationResult(false, "FPS 15-60 arasında olmalı");
            }

            // Çözünürlük kontrolü
            if (Width < 640 || Width > 3840)
            {
                return new ValidationResult(false, "Genişlik 640-3840 arasında olmalı");
            }

            if (Height < 360 || Height > 2160)
            {
                return new ValidationResult(false, "Yükseklik 360-2160 arasında olmalı");
            }

            // Kayıt klasörü kontrolü
            if (EnableLocalRecord && string.IsNullOrWhiteSpace(RecordFolder))
            {
                return new ValidationResult(false, "Yerel kayıt aktifse kayıt klasörü belirtilmeli");
            }

            if (EnableLocalRecord && !string.IsNullOrWhiteSpace(RecordFolder))
            {
                if (!System.IO.Directory.Exists(RecordFolder))
                {
                    try
                    {
                        System.IO.Directory.CreateDirectory(RecordFolder);
                    }
                    catch
                    {
                        return new ValidationResult(false, "Kayıt klasörü oluşturulamadı");
                    }
                }
            }

            return new ValidationResult(true, null);
        }

        private async Task ClearSaveMessageAfterDelay()
        {
            await Task.Delay(3000);
            if (SaveSuccess)
            {
                SaveMessage = null;
            }
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
            if (_devices == null) return;

            var videos = await _devices.GetVideoDevicesAsync();
            var audios = await _devices.GetAudioDevicesAsync();

            VideoDevices.Clear();
            foreach (var v in videos) VideoDevices.Add(v);

            AudioDevices.Clear();
            foreach (var a in audios) AudioDevices.Add(a);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            VideoDevices.Clear();
            AudioDevices.Clear();
            PropertyChanged = null;
            OnSettingsSaved = null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    #region DÜZELTME v18: Helper Types

    /// <summary>
    /// Validation sonucu
    /// </summary>
    internal record ValidationResult(bool IsValid, string? ErrorMessage);

    /// <summary>
    /// Ayarlar kaydedildi event argümanları
    /// </summary>
    public class SettingsSavedEventArgs : EventArgs
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public DateTime Timestamp { get; init; }
    }

    #endregion
}