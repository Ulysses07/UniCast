using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading;
using UniCast.Core.Chat;

namespace UniCast.App.ViewModels
{
    /// <summary>
    /// Chat akışı ViewModel.
    /// DÜZELTME: Constants kullanımı ve proper dispose pattern.
    /// </summary>
    public sealed class ChatViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly ConcurrentQueue<ChatMessage> _incomingBuffer = new();
        private readonly DispatcherTimer _batchTimer;
        private bool _disposed;

        private Action<ChatMessage>? _onMergedHandler;
        private ChatBus? _boundBus;

        public ObservableCollection<ChatMessage> Feed { get; } = new();
        public ICollectionView View { get; }

        // Filtreleme Ayarları
        private bool _yt = true, _tt = true, _ig = true, _fb = true;

        public bool ShowYouTube
        {
            get => _yt;
            set { _yt = value; OnPropertyChanged(); View.Refresh(); }
        }

        public bool ShowTikTok
        {
            get => _tt;
            set { _tt = value; OnPropertyChanged(); View.Refresh(); }
        }

        public bool ShowInstagram
        {
            get => _ig;
            set { _ig = value; OnPropertyChanged(); View.Refresh(); }
        }

        public bool ShowFacebook
        {
            get => _fb;
            set { _fb = value; OnPropertyChanged(); View.Refresh(); }
        }

        public ChatViewModel()
        {
            View = CollectionViewSource.GetDefaultView(Feed);
            View.Filter = o =>
            {
                if (o is not ChatMessage m) return false;
                return m.Platform switch
                {
                    ChatPlatform.YouTube => ShowYouTube,
                    ChatPlatform.TikTok => ShowTikTok,
                    ChatPlatform.Instagram => ShowInstagram,
                    ChatPlatform.Facebook => ShowFacebook,
                    _ => true
                };
            };

            _batchTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _batchTimer.Tick += ProcessMessageBatch;
            _batchTimer.Start();
        }

        public void Bind(ChatBus bus)
        {
            if (_disposed) return;

            Unbind();

            _boundBus = bus;
            _onMergedHandler = m => _incomingBuffer.Enqueue(m);
            bus.OnMerged += _onMergedHandler;
        }

        public void Unbind()
        {
            if (_boundBus != null && _onMergedHandler != null)
            {
                _boundBus.OnMerged -= _onMergedHandler;
            }
            _boundBus = null;
            _onMergedHandler = null;
        }

        private void ProcessMessageBatch(object? sender, EventArgs e)
        {
            if (_disposed || _incomingBuffer.IsEmpty) return;

            while (_incomingBuffer.TryDequeue(out var m))
            {
                Feed.Add(m);
            }

            // DÜZELTME: Constants kullanımı
            while (Feed.Count > ChatConstants.MaxUiMessages)
            {
                Feed.RemoveAt(0);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _batchTimer.Stop();
            _batchTimer.Tick -= ProcessMessageBatch;

            Unbind();

            while (_incomingBuffer.TryDequeue(out _)) { }

            Feed.Clear();

            PropertyChanged = null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}