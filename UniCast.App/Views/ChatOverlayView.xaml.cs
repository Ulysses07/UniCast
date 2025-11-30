using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UniCast.App.Services;
using UniCast.Core.Models;
using UserControl = System.Windows.Controls.UserControl;

namespace UniCast.App.Views
{
    /// <summary>
    /// Overlay chat mesajı modeli.
    /// </summary>
    public class OverlayChatMessage : INotifyPropertyChanged
    {
        private string _author = "";
        private string _text = "";

        public string Author
        {
            get => _author;
            set { _author = value; OnPropertyChanged(); }
        }

        public string Text
        {
            get => _text;
            set { _text = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>
    /// DÜZELTME: ChatOverlayView için ViewModel.
    /// DataContext = this anti-pattern'i düzeltildi.
    /// </summary>
    public sealed class ChatOverlayViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<OverlayItem> SceneItems { get; } = [];
        public ObservableCollection<OverlayChatMessage> ChatMessages { get; } = [];

        public ChatOverlayViewModel()
        {
            LoadFromSettings();
        }

        public void LoadFromSettings()
        {
            var s = SettingsStore.Load();
            if (s.SceneItems == null || s.SceneItems.Count == 0)
            {
                s.Normalize();
                SettingsStore.Save(s);
            }

            SceneItems.Clear();
            var itemsToLoad = s.SceneItems ?? new List<OverlayItem>();
            foreach (var item in itemsToLoad)
                SceneItems.Add(item);
        }

        public void AddMessage(string? author, string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            ChatMessages.Add(new OverlayChatMessage
            {
                Author = author ?? "Kullanıcı",
                Text = message
            });

            // DÜZELTME: Constants kullanımı
            while (ChatMessages.Count > Core.Chat.ChatConstants.MaxOverlayMessages)
            {
                ChatMessages.RemoveAt(0);
            }
        }

        public void SetPosition(double x, double y)
        {
            var chatItem = SceneItems.FirstOrDefault(i => i.Type == OverlayType.Chat);
            if (chatItem != null)
            {
                chatItem.X = x;
                chatItem.Y = y;
            }
        }

        public void SetSize(double width, double height)
        {
            var chatItem = SceneItems.FirstOrDefault(i => i.Type == OverlayType.Chat);
            if (chatItem != null)
            {
                chatItem.Width = width;
                chatItem.Height = height;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>
    /// Chat Overlay View.
    /// DÜZELTME: DataContext = this anti-pattern düzeltildi, proper ViewModel kullanımı.
    /// </summary>
    public partial class ChatOverlayView : UserControl
    {
        private readonly ChatOverlayViewModel _viewModel;

        // Mola Modu Değişkenleri
        private DispatcherTimer? _breakTimer;
        private TimeSpan _remainingBreakTime;

        public ChatOverlayView()
        {
            InitializeComponent();

            // DÜZELTME: Proper ViewModel kullanımı (DataContext = this yerine)
            _viewModel = new ChatOverlayViewModel();
            DataContext = _viewModel;
        }

        /// <summary>
        /// Sahneyi yeniden yükler.
        /// </summary>
        public void RefreshScene() => _viewModel.LoadFromSettings();

        /// <summary>
        /// Overlay'e mesaj ekler.
        /// </summary>
        public void AddMessage(string? author, string? message)
        {
            _viewModel.AddMessage(author, message);
        }

        /// <summary>
        /// Overlay pozisyonunu ayarlar.
        /// </summary>
        public void SetPosition(double x, double y)
        {
            _viewModel.SetPosition(x, y);
        }

        /// <summary>
        /// Overlay boyutunu ayarlar.
        /// </summary>
        public void SetSize(double width, double height)
        {
            _viewModel.SetSize(width, height);
        }

        #region Mola Modu

        public void StartBreak(int minutes)
        {
            _remainingBreakTime = TimeSpan.FromMinutes(minutes);
            UpdateBreakText();

            if (_breakTimer == null)
            {
                _breakTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _breakTimer.Tick += BreakTimer_Tick;
            }
            _breakTimer.Start();

            BreakOverlay.Visibility = Visibility.Visible;
        }

        public void StopBreak()
        {
            _breakTimer?.Stop();
            BreakOverlay.Visibility = Visibility.Collapsed;
        }

        private void BreakTimer_Tick(object? sender, EventArgs e)
        {
            _remainingBreakTime = _remainingBreakTime.Subtract(TimeSpan.FromSeconds(1));

            if (_remainingBreakTime.TotalSeconds <= 0)
            {
                _remainingBreakTime = TimeSpan.Zero;
                _breakTimer?.Stop();
            }

            UpdateBreakText();
        }

        private void UpdateBreakText()
        {
            BreakTimerText.Text = _remainingBreakTime.ToString(@"mm\:ss");
        }

        #endregion

        private void MediaElement_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (sender is MediaElement media)
            {
                media.Position = TimeSpan.Zero;
                media.Play();
            }
        }
    }
}