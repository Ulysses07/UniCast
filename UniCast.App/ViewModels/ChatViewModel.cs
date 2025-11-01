using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using UniCast.Core.Chat;

namespace UniCast.App.ViewModels
{
    public sealed class ChatViewModel : INotifyPropertyChanged
    {
        private readonly object _lock = new();
        public ObservableCollection<ChatMessage> Feed { get; } = new();
        public ICollectionView View { get; }

        private bool _yt = true, _tt = true, _ig = true, _fb = true;
        public bool ShowYouTube { get => _yt; set { _yt = value; OnPropertyChanged(); View.Refresh(); } }
        public bool ShowTikTok { get => _tt; set { _tt = value; OnPropertyChanged(); View.Refresh(); } }
        public bool ShowInstagram { get => _ig; set { _ig = value; OnPropertyChanged(); View.Refresh(); } }
        public bool ShowFacebook { get => _fb; set { _fb = value; OnPropertyChanged(); View.Refresh(); } }

        public ChatViewModel()
        {
            View = CollectionViewSource.GetDefaultView(Feed);
            View.Filter = o =>
            {
                if (o is not ChatMessage m) return false;
                return m.Source switch
                {
                    ChatSource.YouTube => ShowYouTube,
                    ChatSource.TikTok => ShowTikTok,
                    ChatSource.Instagram => ShowInstagram,
                    ChatSource.Facebook => ShowFacebook,
                    _ => false
                };
            };
        }

        public void Bind(UniCast.Core.Chat.ChatBus bus)
        {
            bus.OnMerged += m =>
            {
                // UI thread güvenliği için
                App.Current?.Dispatcher?.Invoke(() =>
                {
                    lock (_lock)
                    {
                        // Çok uzamasın: son 1.000 mesajı tut
                        if (Feed.Count > 1000) Feed.RemoveAt(0);
                        Feed.Add(m);
                    }
                });
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
