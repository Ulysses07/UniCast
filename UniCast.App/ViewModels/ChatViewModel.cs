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
    // DÜZELTME: IDisposable eklendi
    public sealed class ChatViewModel : INotifyPropertyChanged, IDisposable
    {
        // UI Thread Kilitlememek için Tampon Bellek (Buffer)
        private readonly ConcurrentQueue<ChatMessage> _incomingBuffer = new();
        private readonly DispatcherTimer _batchTimer;
        private bool _disposed;

        // DÜZELTME: ChatBus event handler'ı için field
        private Action<ChatMessage>? _onMergedHandler;
        private ChatBus? _boundBus;

        public ObservableCollection<ChatMessage> Feed { get; } = new();
        public ICollectionView View { get; }

        // --- Filtreleme Ayarları ---
        private bool _yt = true, _tt = true, _ig = true, _fb = true;
        public bool ShowYouTube { get => _yt; set { _yt = value; OnPropertyChanged(); View.Refresh(); } }
        public bool ShowTikTok { get => _tt; set { _tt = value; OnPropertyChanged(); View.Refresh(); } }
        public bool ShowInstagram { get => _ig; set { _ig = value; OnPropertyChanged(); View.Refresh(); } }
        public bool ShowFacebook { get => _fb; set { _fb = value; OnPropertyChanged(); View.Refresh(); } }

        public ChatViewModel()
        {
            // Filtreleme Mantığı
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

            // Batch Timer Kurulumu - Her 250ms'de bir UI günceller
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

            // DÜZELTME: Önceki binding'i temizle
            Unbind();

            _boundBus = bus;
            _onMergedHandler = m => _incomingBuffer.Enqueue(m);
            bus.OnMerged += _onMergedHandler;
        }

        // DÜZELTME: Unbind metodu eklendi
        public void Unbind()
        {
            if (_boundBus != null && _onMergedHandler != null)
            {
                _boundBus.OnMerged -= _onMergedHandler;
            }
            _boundBus = null;
            _onMergedHandler = null;
        }

        // Timer her tetiklendiğinde (UI Thread üzerinde) çalışır
        private void ProcessMessageBatch(object? sender, EventArgs e)
        {
            if (_disposed || _incomingBuffer.IsEmpty) return;

            // Kuyruktaki tüm mesajları tek seferde alıp UI listesine ekliyoruz
            while (_incomingBuffer.TryDequeue(out var m))
            {
                Feed.Add(m);
            }

            // Hafıza Yönetimi: Liste çok şişerse eskileri temizle
            while (Feed.Count > Constants.Chat.MaxUiMessages)
            {
                Feed.RemoveAt(0);
            }
        }

        // DÜZELTME: Dispose metodu eklendi
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Timer'ı durdur
            _batchTimer.Stop();
            _batchTimer.Tick -= ProcessMessageBatch;

            // ChatBus'tan unbind ol
            Unbind();

            // Buffer'ı temizle
            while (_incomingBuffer.TryDequeue(out _)) { }

            // Feed'i temizle
            Feed.Clear();

            // Event'i temizle
            PropertyChanged = null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}