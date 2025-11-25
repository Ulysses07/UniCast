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
        public void UpdateSize(double width)
        {
            _view.Dispatcher.Invoke(() =>
            {
                _view.SetWidth(width);
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