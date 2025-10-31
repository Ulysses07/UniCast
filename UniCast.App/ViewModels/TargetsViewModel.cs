using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Input;
using UniCast.App.Infrastructure;
using UniCast.App.Services;
using UniCast.Core.Models;

namespace UniCast.App.ViewModels
{
    public sealed class TargetsViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<TargetItem> Targets { get; } = new();

        private string _warnings = string.Empty;
        public string Warnings
        {
            get => _warnings;
            private set { _warnings = value; OnPropertyChanged(); }
        }

        public ICommand AddTargetCommand { get; }
        public ICommand RemoveTargetCommand { get; }

        public TargetsViewModel()
        {
            AddTargetCommand = new RelayCommand(_ => AddTarget());
            RemoveTargetCommand = new RelayCommand(item =>
            {
                if (item is TargetItem i && Targets.Contains(i))
                {
                    Targets.Remove(i);
                }
            });

            // Load persisted targets
            var saved = TargetsStore.Load();
            if (saved.Count == 0)
            {
                Targets.Add(new TargetItem { Url = "rtmp://example.com/live/stream", Enabled = true });
            }
            else
            {
                foreach (var t in saved) Targets.Add(t);
            }

            Targets.CollectionChanged += OnTargetsChanged;

            // item-level change tracking for validation
            foreach (var t in Targets) t.PropertyChanged += OnItemChanged;

            RecomputeWarnings();
        }

        private void OnTargetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (var it in e.NewItems)
                    if (it is TargetItem t) t.PropertyChanged += OnItemChanged;

            if (e.OldItems != null)
                foreach (var it in e.OldItems)
                    if (it is TargetItem t) t.PropertyChanged -= OnItemChanged;

            Persist();
            RecomputeWarnings();
        }

        private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
        {
            Persist();
            if (e.PropertyName is nameof(TargetItem.Url) or nameof(TargetItem.Enabled))
                RecomputeWarnings();
        }

        private void Persist() => TargetsStore.Save(Targets);

        private void AddTarget()
        {
            var t = new TargetItem { Url = "", Enabled = true };
            t.PropertyChanged += OnItemChanged;
            Targets.Add(t);
            Persist();
            RecomputeWarnings();
        }

        // Basit URL doğrulama: rtmp/rtmps ile başlamalı ve boş olmamalı
        private static readonly Regex RtmpRegex = new(@"^rtmps?:\/\/.+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private void RecomputeWarnings()
        {
            int total = Targets.Count;
            int active = 0, invalid = 0, empty = 0;

            foreach (var t in Targets)
            {
                if (t.Enabled) active++;
                var u = (t.Url ?? "").Trim();
                if (string.IsNullOrWhiteSpace(u)) empty++;
                else if (!RtmpRegex.IsMatch(u)) invalid++;
            }

            if (total == 0)
            {
                Warnings = "Henüz hedef eklenmedi. Yayın başlatmak için en az bir RTMP/RTMPS adresi ekleyin.";
            }
            else if (active == 0)
            {
                Warnings = "Etkin hedef yok. En az bir hedefi 'Enabled' yapın.";
            }
            else if (invalid > 0 || empty > 0)
            {
                Warnings = $"Uyarı: {empty} boş, {invalid} geçersiz URL var. RTMP/RTMPS formatında olmalı.";
            }
            else
            {
                Warnings = "";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
