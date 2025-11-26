using System;
using System.Collections.Generic; // List için eklendi
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UniCast.App.Services;
using UniCast.Core.Models;

namespace UniCast.App.Views
{
    // Basit mesaj modeli (UI için)
    public class OverlayChatMessage
    {
        public string Author { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public partial class ChatOverlayView : System.Windows.Controls.UserControl
    {
        // Sahnedeki Öğeler (Resim, Video, Chat Kutusu)
        public ObservableCollection<OverlayItem> SceneItems { get; private set; } = [];

        // Chat Kutusunun İçindeki Mesajlar
        public ObservableCollection<OverlayChatMessage> ChatMessages { get; private set; } = [];

        public ChatOverlayView()
        {
            InitializeComponent();

            // Başlangıçta ayarları yükle
            LoadFromSettings();
        }

        private void LoadFromSettings()
        {
            var s = SettingsStore.Load();

            // SettingsData.SceneItems listesini buraya kopyalıyoruz
            // Eğer settings boşsa varsayılan ekle
            if (s.SceneItems == null || s.SceneItems.Count == 0)
            {
                s.Normalize(); // Varsayılan Chat kutusunu oluşturur ve listeyi başlatır
                SettingsStore.Save(s);
            }

            SceneItems.Clear();

            // UYARI DÜZELTME (CS8602):
            // Derleyiciye 's.SceneItems'ın null olmadığını garanti ediyoruz.
            // Eğer bir aksilik olur da null gelirse, boş bir liste vererek döngünün patlamasını önlüyoruz.
            var itemsToLoad = s.SceneItems ?? new List<OverlayItem>();

            foreach (var item in itemsToLoad)
            {
                SceneItems.Add(item);
            }
        }

        // Controller'ın çağırdığı metot
        public void AddMessage(string author, string message)
        {
            ChatMessages.Add(new OverlayChatMessage { Author = author, Text = message });

            // Listeyi temiz tut (Son 8 mesaj)
            if (ChatMessages.Count > 8)
            {
                ChatMessages.RemoveAt(0);
            }
        }
        public void RefreshScene()
        {
            LoadFromSettings();
        }

        // Eski metotlar (Uyumluluk için tutuyoruz, ama artık SceneItems kullanılıyor)
        public void SetPosition(double x, double y) { /* SceneItems üzerinden yönetiliyor */ }
        public void SetWidth(double width) { /* SceneItems üzerinden yönetiliyor */ }

        // VİDEO DÖNGÜSÜ (LOOP)
        private void MediaElement_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (sender is MediaElement media)
            {
                media.Position = TimeSpan.Zero; // Başa sar
                media.Play(); // Tekrar oynat
            }
        }
    }
}