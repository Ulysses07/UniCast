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
        /// Toggle buton click handler - Yayın başlat/durdur
        /// </summary>
        private void StreamToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.IsRunning)
            {
                // Durdur
                if (_vm.StopCommand.CanExecute(null))
                    _vm.StopCommand.Execute(null);
            }
            else
            {
                // Başlat
                if (_vm.StartCommand.CanExecute(null))
                    _vm.StartCommand.Execute(null);
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
    /// </summary>
    public class ControlViewDataContext
    {
        public ControlViewModel ControlViewModel { get; }
        public ChatViewModel ChatViewModel { get; }

        public ControlViewDataContext(ControlViewModel controlVm, ChatViewModel chatVm)
        {
            ControlViewModel = controlVm;
            ChatViewModel = chatVm;
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

        // Commands
        public System.Windows.Input.ICommand StartCommand => ControlViewModel.StartCommand;
        public System.Windows.Input.ICommand StopCommand => ControlViewModel.StopCommand;
        public System.Windows.Input.ICommand StartPreviewCommand => ControlViewModel.StartPreviewCommand;
        public System.Windows.Input.ICommand ToggleBreakCommand => ControlViewModel.ToggleBreakCommand;
        public System.Windows.Input.ICommand ToggleMuteCommand => ControlViewModel.ToggleMuteCommand;
    }
}