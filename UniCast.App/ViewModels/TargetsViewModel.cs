using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using UniCast.App.Infrastructure;
using UniCast.App.Services;
using UniCast.Core.Models;
using UniCast.Core.Streaming;

namespace UniCast.App.ViewModels
{
    public sealed class TargetsViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<TargetItem> Targets { get; } = new();

        // --- XAML'ın Aradığı Eksik Özellikler ---

        // 1. Platform Seçenekleri (ComboBox için)
        public ObservableCollection<StreamPlatform> PlatformOptions { get; } = new();

        // 2. Seçili Platform
        private StreamPlatform _selectedPlatform = StreamPlatform.Custom;
        public StreamPlatform SelectedPlatform
        {
            get => _selectedPlatform;
            set { _selectedPlatform = value; OnPropertyChanged(); }
        }

        // 3. Alanlar (Fields)
        private string _displayName = "";
        public string DisplayName { get => _displayName; set { _displayName = value; OnPropertyChanged(); } }

        private string _url = "";
        public string Url { get => _url; set { _url = value; OnPropertyChanged(); } }

        private string _streamKey = "";
        public string StreamKey { get => _streamKey; set { _streamKey = value; OnPropertyChanged(); } }

        // 4. Komutlar (Commands)
        public ICommand AddCommand { get; }
        public ICommand RemoveCommand { get; }

        public TargetsViewModel()
        {
            // Platform Listesini Doldur
            foreach (StreamPlatform p in Enum.GetValues(typeof(StreamPlatform)))
            {
                PlatformOptions.Add(p);
            }

            // Mevcut hedefleri yükle
            RefreshTargets();

            // Komutları Bağla
            AddCommand = new RelayCommand(_ => AddTarget());
            RemoveCommand = new RelayCommand(obj =>
            {
                if (obj is TargetItem item) RemoveTarget(item);
            });
        }

        private void RefreshTargets()
        {
            Targets.Clear();
            var loaded = TargetsStore.Load();
            foreach (var t in loaded) Targets.Add(t);
        }

        private void AddTarget()
        {
            if (string.IsNullOrWhiteSpace(DisplayName) || string.IsNullOrWhiteSpace(Url))
                return; // Basit validasyon

            // URL ve Key birleştirme mantığı (Platforma göre değişebilir)
            string fullUrl = Url;
            if (!string.IsNullOrWhiteSpace(StreamKey))
            {
                if (!fullUrl.EndsWith("/")) fullUrl += "/";
                fullUrl += StreamKey;
            }

            var newItem = new TargetItem
            {
                Id = Guid.NewGuid().ToString(),
                Platform = SelectedPlatform,
                DisplayName = DisplayName,
                Url = fullUrl,
                StreamKey = StreamKey,
                Enabled = true
            };

            Targets.Add(newItem);
            TargetsStore.Save(Targets.ToList());

            // Alanları temizle
            DisplayName = "";
            Url = "";
            StreamKey = "";
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Url));
            OnPropertyChanged(nameof(StreamKey));
        }

        private void RemoveTarget(TargetItem item)
        {
            Targets.Remove(item);
            TargetsStore.Save(Targets.ToList());
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}