using OpenCvSharp;
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using UniCast.App.Views;     // ChatOverlayView (WPF view - App projesinden)
using UniCast.Core.Chat;
using static System.Net.Mime.MediaTypeNames;

namespace UniCast.Overlay
{
    /// <summary>
    /// ChatBus'tan mesajları alır, ChatOverlayView'e basar, OverlayPipePublisher ile FFmpeg'e akıtır.
    /// </summary>
    public sealed class ChatOverlayController : IAsyncDisposable
    {
        private readonly ChatBus _bus;
        private readonly ChatOverlayView _view;
        private readonly Window _hiddenWindow;
        private readonly OverlayPipePublisher _publisher;
        private readonly CancellationTokenSource _cts = new();

        public ChatOverlayController(ChatBus bus, double width, double height, string pipeName = "unicast_overlay")
        {
            _bus = bus;

            _view = new ChatOverlayView
            {
                Width = width,
                Height = height,
                Background = Brushes.Transparent
            };

            _hiddenWindow = new Window
            {
                Title = "UniCast Overlay (Hidden)",
                Width = width,
                Height = height,
                Background = Brushes.Transparent,
                AllowsTransparency = true,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Top = -5000,
                Left = -5000, // ekran dışında
                Content = _view
            };

            _publisher = new OverlayPipePublisher(_view, pipeName, fps: 20);

            _bus.OnMessage += OnChatMessage;
            Task.Run(CleanupLoop, _cts.Token);
        }

        public void Start()
        {
            _hiddenWindow.Show();     // ekran dışında render olsun diye
            _publisher.Start();       // pipe yayını
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            _bus.OnMessage -= OnChatMessage;
            await _publisher.StopAsync();
            _hiddenWindow.Close();
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        private void OnChatMessage(ChatMessage msg)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _view.Push(msg.Author, msg.Text);
            });
        }

        private async Task CleanupLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, _cts.Token);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _view.ClearOld(TimeSpan.FromSeconds(30));
                    });
                }
                catch { }
            }
        }
    }
}
