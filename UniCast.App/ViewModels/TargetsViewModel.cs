using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using UniCast.App.Infrastructure;
using UniCast.App.Services;
using UniCast.Core.Models;

namespace UniCast.App.ViewModels
{
    public sealed class TargetsViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<TargetItem> Targets { get; } = new();

        public ICommand AddTargetCommand { get; }
        public ICommand RemoveTargetCommand { get; }

        public TargetsViewModel()
        {
            AddTargetCommand = new RelayCommand(_ => AddTarget());
            RemoveTargetCommand = new RelayCommand(item =>
            {
                if (item is TargetItem i && Targets.Contains(i)) Targets.Remove(i);
            });

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
        }

        private void OnTargetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => Persist();

        private void Persist() => TargetsStore.Save(Targets);

        private void AddTarget()
        {
            Targets.Add(new TargetItem { Url = "", Enabled = true });
            Persist();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
