using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq; // FirstOrDefault için gerekli
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        public ChatOverlayView()
        {
            InitializeComponent();
            LoadFromSettings();
        }

        public void RefreshScene()
        {
            LoadFromSettings();
        }

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
            foreach (var item in itemsToLoad)
            {
                SceneItems.Add(item);
            }
        }

        // HATA DÜZELTME: ChatContainer yok, listeden Chat öğesini bulup güncelliyoruz.
        public void SetPosition(double x, double y)
        {
            var chatItem = SceneItems.FirstOrDefault(i => i.Type == OverlayType.Chat);
            if (chatItem != null)
            {
                chatItem.X = x;
                chatItem.Y = y;
            }
        }

        // HATA DÜZELTME: SetWidth yerine SetSize (Yükseklik de geldi)
        public void SetSize(double width, double height)
        {
            var chatItem = SceneItems.FirstOrDefault(i => i.Type == OverlayType.Chat);
            if (chatItem != null)
            {
                chatItem.Width = width;
                chatItem.Height = height;
            }
        }

        public void AddMessage(string author, string message)
        {
            ChatMessages.Add(new OverlayChatMessage { Author = author, Text = message });
            if (ChatMessages.Count > 8) ChatMessages.RemoveAt(0);
        }

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