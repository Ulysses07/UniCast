using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Serilog;
using UniCast.App.Infrastructure;
using UniCast.App.Services;
using UniCast.App.Services.Capture;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace UniCast.App.Views
{
    /// <summary>
    /// DÜZELTME v19: İlk çalıştırma sihirbazı
    /// Yeni kullanıcılar için temel ayarları yapılandırır
    /// </summary>
    public partial class FirstRunWizard : Window
    {
        #region Fields

        private int _currentStep = 0;
        private const int TotalSteps = 5;
        private readonly IDeviceService _deviceService;
        private readonly List<Ellipse> _stepIndicators;

        #endregion

        #region Properties

        /// <summary>
        /// Wizard tamamlandı mı?
        /// </summary>
        public bool IsCompleted { get; private set; }

        /// <summary>
        /// Wizard sonuçları
        /// </summary>
        public WizardResult Result { get; private set; } = new();

        #endregion

        #region Constructor

        public FirstRunWizard()
        {
            InitializeComponent();

            _deviceService = new DeviceService();
            _stepIndicators = new List<Ellipse>
            {
                Step1Indicator, Step2Indicator, Step3Indicator,
                Step4Indicator, Step5Indicator
            };

            Loaded += OnLoaded;
        }

        #endregion

        #region Initialization

        // DÜZELTME v20: AsyncVoidHandler ile güvenli async event handler
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            AsyncVoidHandler.Handle(
                async () =>
                {
                    await LoadDevicesAsync();
                    UpdateUI();
                },
                showErrorDialog: false);
        }

        private async Task LoadDevicesAsync()
        {
            try
            {
                var videos = await _deviceService.GetVideoDevicesAsync();
                var audios = await _deviceService.GetAudioDevicesAsync();

                CameraCombo.Items.Clear();
                foreach (var device in videos)
                {
                    CameraCombo.Items.Add(device.Name);
                }
                if (CameraCombo.Items.Count > 0)
                    CameraCombo.SelectedIndex = 0;

                MicrophoneCombo.Items.Clear();
                foreach (var device in audios)
                {
                    MicrophoneCombo.Items.Add(device.Name);
                }
                if (MicrophoneCombo.Items.Count > 0)
                    MicrophoneCombo.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FirstRunWizard] Cihaz yükleme hatası");
            }
        }

        #endregion

        #region Navigation

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep < TotalSteps - 1)
            {
                _currentStep++;
                WizardTabs.SelectedIndex = _currentStep;
                UpdateUI();
            }
            else
            {
                // Finish
                SaveAndComplete();
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep > 0)
            {
                _currentStep--;
                WizardTabs.SelectedIndex = _currentStep;
                UpdateUI();
            }
        }

        private void RefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            AsyncVoidHandler.Handle(
                async () => await LoadDevicesAsync(),
                showErrorDialog: false);
        }

        private void UpdateUI()
        {
            // Back button visibility
            BtnBack.Visibility = _currentStep > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Next button text
            BtnNext.Content = _currentStep == TotalSteps - 1 ? "✓ Tamamla" : "İleri →";

            // Step indicators
            for (int i = 0; i < _stepIndicators.Count; i++)
            {
                _stepIndicators[i].Fill = i <= _currentStep
                    ? new SolidColorBrush(Color.FromRgb(67, 97, 238)) // #4361ee
                    : new SolidColorBrush(Color.FromRgb(61, 61, 92));  // #3d3d5c
            }
        }

        #endregion

        #region Save & Complete

        private void SaveAndComplete()
        {
            try
            {
                // Sonuçları topla
                CollectResults();

                // Ayarları kaydet
                SaveSettings();

                // First run flag'i kaydet
                if (ChkDontShowAgain.IsChecked == true)
                {
                    MarkFirstRunComplete();
                }

                IsCompleted = true;

                Log.Information("[FirstRunWizard] Tamamlandı");

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FirstRunWizard] Kaydetme hatası");
                MessageBox.Show($"Ayarlar kaydedilemedi: {ex.Message}", "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CollectResults()
        {
            // Cihazlar
            Result.SelectedCamera = CameraCombo.SelectedItem?.ToString() ?? "";
            Result.SelectedMicrophone = MicrophoneCombo.SelectedItem?.ToString() ?? "";

            // Kalite - DÜZELTME v20: AppConstants kullanımı
            if (QualityLow.IsChecked == true)
            {
                Result.QualityPreset = QualityPreset.Low;
                Result.VideoBitrate = 2500;
                Result.Width = 1280;
                Result.Height = 720;
                Result.Fps = AppConstants.Video.DefaultFps;
            }
            else if (QualityHigh.IsChecked == true)
            {
                Result.QualityPreset = QualityPreset.High;
                Result.VideoBitrate = 6000;
                Result.Width = 1920;
                Result.Height = 1080;
                Result.Fps = AppConstants.Video.DefaultFps;
            }
            else // Medium (default)
            {
                Result.QualityPreset = QualityPreset.Medium;
                Result.VideoBitrate = AppConstants.Video.DefaultBitrateKbps;
                Result.Width = 1280;
                Result.Height = 720;
                Result.Fps = AppConstants.Video.DefaultFps;
            }

            // Platformlar
            Result.EnableYouTube = ChkYouTube.IsChecked == true;
            Result.EnableTwitch = ChkTwitch.IsChecked == true;
            Result.EnableTikTok = ChkTikTok.IsChecked == true;
            Result.EnableInstagram = ChkInstagram.IsChecked == true;
            Result.EnableFacebook = ChkFacebook.IsChecked == true;
        }

        private void SaveSettings()
        {
            SettingsStore.Update(s =>
            {
                // Cihazlar
                s.DefaultCamera = Result.SelectedCamera;
                s.DefaultMicrophone = Result.SelectedMicrophone;
                s.SelectedVideoDevice = Result.SelectedCamera;
                s.SelectedAudioDevice = Result.SelectedMicrophone;

                // Kalite
                s.VideoKbps = Result.VideoBitrate;
                s.Width = Result.Width;
                s.Height = Result.Height;
                s.Fps = Result.Fps;
                s.AudioKbps = AppConstants.Audio.DefaultBitrateKbps;
            });

            SettingsStore.Save();
        }

        private void MarkFirstRunComplete()
        {
            // DÜZELTME v20: AsyncFileHelper kullanabilir ama sync versiyonu OK
            try
            {
                var flagPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppConstants.Paths.AppFolderName, ".first_run_complete");

                var dir = System.IO.Path.GetDirectoryName(flagPath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }

                System.IO.File.WriteAllText(flagPath, DateTime.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[FirstRunWizard] First run flag yazılamadı");
            }
        }

        #endregion

        #region Static Helpers

        /// <summary>
        /// İlk çalıştırma gerekli mi?
        /// </summary>
        public static bool ShouldShowWizard()
        {
            try
            {
                var flagPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppConstants.Paths.AppFolderName, ".first_run_complete");

                return !System.IO.File.Exists(flagPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Wizard'ı göster ve sonucu döndür
        /// </summary>
        public static WizardResult? ShowWizard()
        {
            var wizard = new FirstRunWizard();
            var result = wizard.ShowDialog();

            return result == true ? wizard.Result : null;
        }

        #endregion
    }

    #region Result Types

    public enum QualityPreset
    {
        Low,
        Medium,
        High
    }

    public class WizardResult
    {
        // Cihazlar
        public string SelectedCamera { get; set; } = "";
        public string SelectedMicrophone { get; set; } = "";

        // Kalite
        public QualityPreset QualityPreset { get; set; } = QualityPreset.Medium;
        public int VideoBitrate { get; set; } = 4500;
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public int Fps { get; set; } = 30;

        // Platformlar
        public bool EnableYouTube { get; set; }
        public bool EnableTwitch { get; set; }
        public bool EnableTikTok { get; set; }
        public bool EnableInstagram { get; set; }
        public bool EnableFacebook { get; set; }
    }

    #endregion
}