using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading; // Timer için
using UniCast.App.Services;
using UniCast.Core.Models;

namespace UniCast.App.Views
{
    public class OverlayChatMessage
    {
        public string Author { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public partial class ChatOverlayView : System.Windows.Controls.UserControl
    {
        public ObservableCollection<OverlayItem> SceneItems { get; private set; } = new();
        public ObservableCollection<OverlayChatMessage> ChatMessages { get; private set; } = new();

        // Mola Modu Değişkenleri
        private DispatcherTimer? _breakTimer;
        private TimeSpan _remainingBreakTime;

        public ChatOverlayView()
        {
            InitializeComponent();
            LoadFromSettings();
        }

        public void RefreshScene() => LoadFromSettings();

        private void LoadFromSettings()
        {
            var s = SettingsStore.Load();
            if (s.SceneItems == null || s.SceneItems.Count == 0)
            {
                s.Normalize();
                SettingsStore.Save(s);
            }

            SceneItems.Clear();
            var itemsToLoad = s.SceneItems ?? new List<OverlayItem>();
            foreach (var item in itemsToLoad) SceneItems.Add(item);
        }

        // --- MOLA MODU YÖNETİMİ (YENİ) ---

        public void StartBreak(int minutes)
        {
            // 1. Geri Sayım Süresini Ayarla
            _remainingBreakTime = TimeSpan.FromMinutes(minutes);
            UpdateBreakText();

            // 2. Timer Başlat (Saniyede 1 kez çalışır)
            if (_breakTimer == null)
            {
                _breakTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _breakTimer.Tick += BreakTimer_Tick;
            }
            _breakTimer.Start();

            // 3. Ekranı Göster
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
                // Süre bitti ama ekranı kapatmıyoruz, 00:00'da kalsın
                _remainingBreakTime = TimeSpan.Zero;
                _breakTimer?.Stop();
            }

            UpdateBreakText();
        }

        private void UpdateBreakText()
        {
            BreakTimerText.Text = _remainingBreakTime.ToString(@"mm\:ss");
        }

        // --- DİĞER METOTLAR (AYNI) ---

        public void SetPosition(double x, double y)
        {
            var chatItem = SceneItems.FirstOrDefault(i => i.Type == OverlayType.Chat);
            if (chatItem != null) { chatItem.X = x; chatItem.Y = y; }
        }

        public void SetSize(double width, double height)
        {
            var chatItem = SceneItems.FirstOrDefault(i => i.Type == OverlayType.Chat);
            if (chatItem != null) { chatItem.Width = width; chatItem.Height = height; }
        }

        public void AddMessage(string author, string message)
        {
            ChatMessages.Add(new OverlayChatMessage { Author = author, Text = message });
            if (ChatMessages.Count > 8) ChatMessages.RemoveAt(0);
        }

        private void MediaElement_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (sender is MediaElement media) { media.Position = TimeSpan.Zero; media.Play(); }
        }
    }
}