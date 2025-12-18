using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using UniCast.App.Infrastructure;
using UniCast.App.Services;
using UniCast.App.ViewModels;
using UniCast.Core.Chat;
using Serilog;
using UserControl = System.Windows.Controls.UserControl;
using MessageBox = System.Windows.MessageBox;

namespace UniCast.App.Views
{
    public partial class ControlView : UserControl
    {
        private readonly ControlViewModel _vm;
        private readonly ChatViewModel _chatVm;
        private bool _layoutLoaded;

        public ControlView() : this(new ControlViewModel(), new ChatViewModel())
        {
        }

        public ControlView(ControlViewModel vm) : this(vm, new ChatViewModel())
        {
        }

        public ControlView(ControlViewModel vm, ChatViewModel chatVm)
        {
            InitializeComponent();
            _vm = vm;
            _chatVm = chatVm;

            // Combined ViewModel oluştur
            DataContext = new ControlViewDataContext(vm, chatVm);

            Loaded += ControlView_Loaded;
            Unloaded += ControlView_Unloaded;
        }

        /// <summary>
        /// ChatViewModel'i ChatBus'a bağlar.
        /// MainWindow'dan çağrılmalı.
        /// </summary>
        public void BindChatBus(ChatBus bus)
        {
            _chatVm.Bind(bus);
        }

        private void ControlView_Loaded(object sender, RoutedEventArgs e)
        {
            // Layout ayarlarını yükle
            LoadLayoutSettings();

            // ChatBus'a bağlan (her Loaded'da yeniden bağlan)
            _chatVm.Bind(ChatBus.Instance);
            Log.Debug("[ControlView] ChatBus'a bağlandı");

            // Preview başlat
            AsyncVoidHandler.Handle(
                async () => await _vm.StartPreviewAsync(),
                showErrorDialog: false);
        }

        private void ControlView_Unloaded(object sender, RoutedEventArgs e)
        {
            // Layout ayarlarını kaydet
            SaveLayoutSettings();

            // ChatBus bağlantısını kes
            _chatVm.Unbind();

            // Preview durdur
            AsyncVoidHandler.Handle(
                async () => await _vm.StopPreviewAsync(),
                showErrorDialog: false);
        }

        /// <summary>
        /// Yayın butonu click handler - Yayın başlat/durdur
        /// </summary>
        private void StreamButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_vm.IsRunning)
                {
                    // Durdurma onayı iste
                    var result = MessageBox.Show(
                        "Yayını durdurmak istediğinize emin misiniz?\n\nTüm platformlardaki yayınınız sonlandırılacak.",
                        "Yayını Durdur",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question,
                        MessageBoxResult.No);

                    if (result == MessageBoxResult.Yes)
                    {
                        Log.Debug("[ControlView] Yayın durdurma komutu tetikleniyor...");
                        if (_vm.StopCommand.CanExecute(null))
                            _vm.StopCommand.Execute(null);
                    }
                }
                else
                {
                    // Başlat
                    Log.Debug("[ControlView] Yayın başlatma komutu tetikleniyor...");
                    if (_vm.StartCommand.CanExecute(null))
                        _vm.StartCommand.Execute(null);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ControlView] Yayın butonu hatası");
            }
        }

        /// <summary>
        /// GridSplitter sürükleme tamamlandığında layout'u kaydet.
        /// </summary>
        private void GridSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            SaveLayoutSettings();
        }

        /// <summary>
        /// Layout ayarlarını dosyadan yükler.
        /// </summary>
        private void LoadLayoutSettings()
        {
            if (_layoutLoaded) return;

            try
            {
                var settings = SettingsStore.Data;

                // Kolon genişliklerini ayarla
                if (settings.LayoutMolaColumnWidth > 0)
                    MolaColumn.Width = new GridLength(settings.LayoutMolaColumnWidth, GridUnitType.Pixel);

                if (settings.LayoutCameraColumnWidth > 0)
                    CameraColumn.Width = new GridLength(settings.LayoutCameraColumnWidth, GridUnitType.Star);

                if (settings.LayoutChatColumnWidth > 0)
                    ChatColumn.Width = new GridLength(settings.LayoutChatColumnWidth, GridUnitType.Pixel);

                _layoutLoaded = true;
                Log.Debug("[ControlView] Layout ayarları yüklendi: Mola={Mola}, Camera={Camera}, Chat={Chat}",
                    settings.LayoutMolaColumnWidth, settings.LayoutCameraColumnWidth, settings.LayoutChatColumnWidth);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[ControlView] Layout ayarları yüklenemedi, varsayılanlar kullanılıyor");
            }
        }

        /// <summary>
        /// Layout ayarlarını dosyaya kaydeder.
        /// </summary>
        private void SaveLayoutSettings()
        {
            try
            {
                SettingsStore.Update(s =>
                {
                    s.LayoutMolaColumnWidth = MolaColumn.Width.Value;
                    s.LayoutCameraColumnWidth = CameraColumn.Width.Value;
                    s.LayoutChatColumnWidth = ChatColumn.Width.Value;
                });

                Log.Debug("[ControlView] Layout ayarları kaydedildi: Mola={Mola}, Camera={Camera}, Chat={Chat}",
                    MolaColumn.Width.Value, CameraColumn.Width.Value, ChatColumn.Width.Value);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[ControlView] Layout ayarları kaydedilemedi");
            }
        }

        /// <summary>
        /// ViewModel'leri dispose eder.
        /// </summary>
        public void Cleanup()
        {
            _chatVm.Dispose();
            _vm.Dispose();
        }
    }

    /// <summary>
    /// ControlView için combined DataContext.
    /// Hem ControlViewModel hem ChatViewModel property'lerini expose eder.
    /// INotifyPropertyChanged ile UI güncellemelerini destekler.
    /// </summary>
    public class ControlViewDataContext : System.ComponentModel.INotifyPropertyChanged
    {
        public ControlViewModel ControlViewModel { get; }
        public ChatViewModel ChatViewModel { get; }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public ControlViewDataContext(ControlViewModel controlVm, ChatViewModel chatVm)
        {
            ControlViewModel = controlVm;
            ChatViewModel = chatVm;

            // ControlViewModel'den gelen property değişikliklerini forward et
            ControlViewModel.PropertyChanged += (s, e) =>
            {
                // Tüm property değişikliklerini forward et
                PropertyChanged?.Invoke(this, e);
            };
        }

        // ControlViewModel property'lerini doğrudan expose et (binding kolaylığı için)
        public string Status => ControlViewModel.Status;
        public string Metric => ControlViewModel.Metric;
        public string Advisory => ControlViewModel.Advisory;
        public bool IsRunning => ControlViewModel.IsRunning;
        public bool IsOnBreak
        {
            get => ControlViewModel.IsOnBreak;
            set => ControlViewModel.IsOnBreak = value;
        }
        public int BreakDuration
        {
            get => ControlViewModel.BreakDuration;
            set => ControlViewModel.BreakDuration = value;
        }
        public double AudioLevel => ControlViewModel.AudioLevel;
        public bool IsMuted => ControlViewModel.IsMuted;
        public System.Windows.Media.ImageSource? PreviewImage => ControlViewModel.PreviewImage;
        public string StreamDuration => ControlViewModel.StreamDuration;

        // Commands
        public System.Windows.Input.ICommand StartCommand => ControlViewModel.StartCommand;
        public System.Windows.Input.ICommand StopCommand => ControlViewModel.StopCommand;
        public System.Windows.Input.ICommand StartPreviewCommand => ControlViewModel.StartPreviewCommand;
        public System.Windows.Input.ICommand ToggleBreakCommand => ControlViewModel.ToggleBreakCommand;
        public System.Windows.Input.ICommand ToggleMuteCommand => ControlViewModel.ToggleMuteCommand;
    }
}