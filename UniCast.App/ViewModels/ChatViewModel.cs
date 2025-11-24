using System;
using System.Collections.Concurrent; // Queue için
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading; // DispatcherTimer için
using UniCast.Core.Chat;

namespace UniCast.App.ViewModels
{
    public sealed class ChatViewModel : INotifyPropertyChanged
    {
        // UI Thread Kilitlememek için Tampon Bellek (Buffer)
        private readonly ConcurrentQueue<ChatMessage> _incomingBuffer = new();
        private readonly DispatcherTimer _batchTimer;

        public ObservableCollection<ChatMessage> Feed { get; } = new();
        public ICollectionView View { get; }

        // --- Filtreleme Ayarları (Mevcut yapı korundu) ---
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

            // PERFORMANS İYİLEŞTİRMESİ: Batch Timer Kurulumu
            // Her 250ms'de bir (Saniyede 4 kez) çalışır ve UI'ı günceller.
            _batchTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _batchTimer.Tick += ProcessMessageBatch;
            _batchTimer.Start();
        }

        public void Bind(ChatBus bus)
        {
            // Eski 'Dispatcher.Invoke' kaldırıldı.
            // Artık gelen mesajı sadece kuyruğa atıyoruz (Maliyeti neredeyse sıfır).
            // Bu işlem herhangi bir thread'den güvenle yapılabilir.
            bus.OnMerged += m => _incomingBuffer.Enqueue(m);
        }

        // Timer her tetiklendiğinde (UI Thread üzerinde) çalışır
        private void ProcessMessageBatch(object? sender, EventArgs e)
        {
            if (_incomingBuffer.IsEmpty) return;

            // Kuyruktaki tüm mesajları tek seferde alıp UI listesine ekliyoruz
            // Bu yöntem, her mesaj için ayrı ayrı ekran çizdirmekten çok daha verimlidir.
            while (_incomingBuffer.TryDequeue(out var m))
            {
                Feed.Add(m);
            }

            // Hafıza Yönetimi: Liste çok şişerse eskileri temizle
            // (Lock kullanmaya gerek yok çünkü DispatcherTimer zaten UI thread'de çalışır)
            while (Feed.Count > 1000)
            {
                Feed.RemoveAt(0);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}