using System;
using System.Threading.Tasks;
using UniCast.App.Views;

namespace UniCast.App.Overlay
{
    /// <summary>
    /// ChatOverlayView'i gizli bir WPF penceresinde render eder ve OverlayPipePublisher ile FFmpeg'e pipe eder.
    /// Dışarıdan Push(author, text) ile mesaj basılır.
    /// </summary>
    public sealed class ChatOverlayController : IAsyncDisposable
    {
        private readonly UniCast.App.Views.ChatOverlayView _view;
        private readonly System.Windows.Window _hiddenWindow;   // WPF Window
        private readonly OverlayPipePublisher _publisher;

        public ChatOverlayController(double width, double height, string pipeName = "unicast_overlay")
        {
            _view = new UniCast.App.Views.ChatOverlayView
            {
                Width = width,
                Height = height,
                Background = System.Windows.Media.Brushes.Transparent
            };

            _hiddenWindow = new System.Windows.Window
            {
                Title = "UniCast Overlay (Hidden)",
                Width = width,
                Height = height,
                Background = System.Windows.Media.Brushes.Transparent,
                AllowsTransparency = true,
                WindowStyle = System.Windows.WindowStyle.None,
                ShowInTaskbar = false,
                Top = -5000,
                Left = -5000,
                Content = _view
            };

            // DİKKAT: Publisher artık FrameworkElement alıyor → cast GEREKMİYOR
            _publisher = new OverlayPipePublisher(_view, pipeName, fps: 20);
        }

        public void Start()
        {
            _hiddenWindow.Show();
            _publisher.Start();
        }

        public void Push(string author, string text)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() => _view.Push(author, text));
        }

        public async Task StopAsync()
        {
            await _publisher.StopAsync();
            _hiddenWindow.Close();
        }

        public async ValueTask DisposeAsync() => await StopAsync();
    }
}
