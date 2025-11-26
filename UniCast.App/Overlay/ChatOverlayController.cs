using System;
using System.Threading.Tasks;
using System.Windows;
using UniCast.App.Views;

namespace UniCast.App.Overlay
{
    public class ChatOverlayController : IAsyncDisposable
    {
        private readonly ChatOverlayView _view;
        private readonly OverlayPipePublisher _publisher;

        public ChatOverlayController(int width, int height, string pipeName)
        {
            _view = new ChatOverlayView
            {
                Width = width,
                Height = height
            };

            _view.Measure(new System.Windows.Size(width, height));
            _view.Arrange(new System.Windows.Rect(new System.Windows.Size(width, height)));

            _publisher = new OverlayPipePublisher(_view, pipeName, width, height);
        }

        public void Start()
        {
            _publisher.Start();
        }

        // YENİ METOT: SettingsData'yı tekrar yükle ve View'u güncelle
        public void ReloadSettings()
        {
            _view.Dispatcher.Invoke(() =>
            {
                // ChatOverlayView.xaml.cs içinde LoadFromSettings metodu var, onu public yapmalısın.
                // Veya basitçe: View zaten SettingsStore kullanıyor, yeniden tetikleyelim.
                // En temizi: ChatOverlayView.xaml.cs'deki 'LoadFromSettings' metodunu 'public void Refresh()' yap.

                // Burada View üzerinde tanımlayacağımız metodu çağırıyoruz:
                _view.RefreshScene();
            });
            _publisher.Invalidate();
        }

        public async Task StopAsync()
        {
            await _publisher.StopAsync();
        }

        public void UpdatePosition(int x, int y)
        {
            _view.Dispatcher.Invoke(() =>
            {
                _view.SetPosition(x, y);
            });
            _publisher.Invalidate();
        }

        // EKSİK OLAN METOT BU:
        public void UpdateSize(double width, double height)
        {
            _view.Dispatcher.Invoke(() =>
            {
                _view.SetSize(width, height);
            });
            _publisher.Invalidate();
        }

        public void Push(string author, string message)
        {
            _view.Dispatcher.Invoke(() =>
            {
                _view.AddMessage(author, message);
            });
            _publisher.Invalidate();
        }

        public async ValueTask DisposeAsync()
        {
            await _publisher.DisposeAsync();
        }
    }
}