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

        // Platform Seçenekleri (ComboBox için)
        public ObservableCollection<StreamPlatform> PlatformOptions { get; } = new();

        // Seçili Platform
        private StreamPlatform _selectedPlatform = StreamPlatform.Custom;
        public StreamPlatform SelectedPlatform
        {
            get => _selectedPlatform;
            set { _selectedPlatform = value; OnPropertyChanged(); }
        }

        // Alanlar
        private string _displayName = "";
        public string DisplayName { get => _displayName; set { _displayName = value; OnPropertyChanged(); } }

        private string _url = "";
        public string Url { get => _url; set { _url = value; OnPropertyChanged(); } }

        private string _streamKey = "";
        public string StreamKey { get => _streamKey; set { _streamKey = value; OnPropertyChanged(); } }

        // Düzenleme Modu
        private TargetItem? _editingItem = null;
        public TargetItem? EditingItem
        {
            get => _editingItem;
            set
            {
                _editingItem = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEditing));
                OnPropertyChanged(nameof(FormTitle));
                OnPropertyChanged(nameof(SubmitButtonText));
            }
        }

        public bool IsEditing => EditingItem != null;
        public string FormTitle => IsEditing ? "✏️ Hedef Düzenle" : "🔗 Yeni Hedef Ekle";
        public string SubmitButtonText => IsEditing ? "Güncelle" : "Listeye Ekle";

        // Komutlar
        public ICommand AddCommand { get; }
        public ICommand RemoveCommand { get; }
        public ICommand EditCommand { get; }
        public ICommand CancelEditCommand { get; }

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
            AddCommand = new RelayCommand(_ => AddOrUpdateTarget());
            RemoveCommand = new RelayCommand(obj =>
            {
                if (obj is TargetItem item) RemoveTarget(item);
            });
            EditCommand = new RelayCommand(obj =>
            {
                if (obj is TargetItem item) StartEdit(item);
            });
            CancelEditCommand = new RelayCommand(_ => CancelEdit());
        }

        private void RefreshTargets()
        {
            Targets.Clear();
            var loaded = TargetsStore.Load();
            foreach (var t in loaded) Targets.Add(t);
        }

        private void StartEdit(TargetItem item)
        {
            EditingItem = item;
            SelectedPlatform = item.Platform;
            DisplayName = item.DisplayName ?? "";

            // URL ve StreamKey'i ayır
            // Eğer StreamKey kaydedilmişse onu kullan, yoksa URL'den ayırmaya çalış
            if (!string.IsNullOrEmpty(item.StreamKey))
            {
                // StreamKey varsa, URL'den StreamKey'i çıkar
                Url = (item.Url ?? "").Replace("/" + item.StreamKey, "").TrimEnd('/');
                StreamKey = item.StreamKey;
            }
            else
            {
                // StreamKey yoksa, URL'i olduğu gibi kullan
                Url = item.Url ?? "";
                StreamKey = "";
            }
        }

        private void CancelEdit()
        {
            EditingItem = null;
            ClearForm();
        }

        private void AddOrUpdateTarget()
        {
            if (string.IsNullOrWhiteSpace(DisplayName) || string.IsNullOrWhiteSpace(Url))
                return;

            // URL ve Key birleştirme mantığı
            string fullUrl = Url;
            if (!string.IsNullOrWhiteSpace(StreamKey))
            {
                if (!fullUrl.EndsWith("/")) fullUrl += "/";
                fullUrl += StreamKey;
            }

            if (IsEditing && EditingItem != null)
            {
                // Güncelleme modu
                EditingItem.Platform = SelectedPlatform;
                EditingItem.DisplayName = DisplayName;
                EditingItem.Url = fullUrl;
                EditingItem.StreamKey = StreamKey;

                // ObservableCollection'ı yenilemek için
                var index = Targets.IndexOf(EditingItem);
                if (index >= 0)
                {
                    Targets.RemoveAt(index);
                    Targets.Insert(index, EditingItem);
                }

                TargetsStore.Save(Targets.ToList());

                // Toast bildirimi
                Services.ToastService.Instance.ShowSuccess($"✏️ {DisplayName} güncellendi");

                EditingItem = null;
            }
            else
            {
                // Ekleme modu
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

                // Toast bildirimi
                Services.ToastService.Instance.ShowSuccess($"✓ {DisplayName} eklendi");
            }

            ClearForm();
        }

        private void ClearForm()
        {
            DisplayName = "";
            Url = "";
            StreamKey = "";
        }

        private void RemoveTarget(TargetItem item)
        {
            // Eğer düzenlenen öğe siliniyorsa, düzenleme modundan çık
            if (EditingItem == item)
            {
                CancelEdit();
            }

            var name = item.DisplayName;
            Targets.Remove(item);
            TargetsStore.Save(Targets.ToList());

            // Toast bildirimi
            Services.ToastService.Instance.ShowInfo($"🗑️ {name} silindi");
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}