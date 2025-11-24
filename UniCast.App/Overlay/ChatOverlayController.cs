using System;
using System.Threading.Tasks;
using System.Windows;
using UniCast.App.Views;
using Size = System.Windows.Size;

namespace UniCast.App.Overlay
{
    public class ChatOverlayController : IAsyncDisposable
    {
        private readonly ChatOverlayView _view;
        private readonly OverlayPipePublisher _publisher;

        public ChatOverlayController(int width, int height, string pipeName)
        {
            // 1. Görseli (View) oluştur
            _view = new ChatOverlayView
            {
                Width = width,
                Height = height
            };

            // View'in render edilebilmesi için bellekte "Measure/Arrange" yapılması gerekir
            // (Ekranda görünmese bile boyutlarının hesaplanması için)
            _view.Measure(new Size(width, height));
            _view.Arrange(new Rect(new Size(width, height)));

            // 2. Publisher'ı oluştur
            // HATA DÜZELTME: Artık 'fps' parametresi göndermiyoruz.
            _publisher = new OverlayPipePublisher(_view, pipeName, width, height);
        }

        public void Start()
        {
            _publisher.Start();
        }

        public async Task StopAsync()
        {
            await _publisher.StopAsync();
        }

        public void Push(string author, string message)
        {
            // 1. UI Thread üzerinde View'a mesajı ekle
            // (WPF kontrollerine sadece kendi thread'inden erişilebilir)
            _view.Dispatcher.Invoke(() =>
            {
                _view.AddMessage(author, message);
            });

            // 2. PERFORMANS OPTİMİZASYONU:
            // Publisher'a "Hey, görüntü değişti, yeni kare çiz!" diyoruz.
            // Bu sayede chat gelmediğinde CPU harcanmıyor.
            _publisher.Invalidate();
        }

        public async ValueTask DisposeAsync()
        {
            await _publisher.DisposeAsync();
        }
    }
}