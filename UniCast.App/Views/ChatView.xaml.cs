using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UniCast.App.ViewModels;

namespace UniCast.App.Views
{
    public partial class ChatView : System.Windows.Controls.UserControl
    {
        private bool _autoScroll = true;
        private ScrollViewer? _scrollViewer;

        public ChatView()
        {
            InitializeComponent();

            Loaded += ChatView_Loaded;
            Unloaded += ChatView_Unloaded;
        }

        private void ChatView_Loaded(object sender, RoutedEventArgs e)
        {
            // ScrollViewer'ı bul
            _scrollViewer = GetScrollViewer(ChatListView);

            if (_scrollViewer != null)
            {
                _scrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;
            }

            // Feed koleksiyonuna abone ol
            if (DataContext is ChatViewModel vm)
            {
                vm.Feed.CollectionChanged += Feed_CollectionChanged;
            }
        }

        private void ChatView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_scrollViewer != null)
            {
                _scrollViewer.ScrollChanged -= ScrollViewer_ScrollChanged;
            }

            if (DataContext is ChatViewModel vm)
            {
                vm.Feed.CollectionChanged -= Feed_CollectionChanged;
            }
        }

        private void Feed_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Yeni mesaj eklendiğinde ve auto-scroll aktifse, en alta kaydır
            if (e.Action == NotifyCollectionChangedAction.Add && _autoScroll && _scrollViewer != null)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    _scrollViewer.ScrollToEnd();
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scrollViewer == null) return;

            // Kullanıcı en altta mı kontrol et
            // Eğer kullanıcı yukarı scroll yaptıysa, auto-scroll'u kapat
            // En alta gelince tekrar aç
            var isAtBottom = _scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 50;

            if (e.ExtentHeightChange == 0)
            {
                // Kullanıcı scroll yaptı
                _autoScroll = isAtBottom;
            }
        }

        /// <summary>
        /// ListView içindeki ScrollViewer'ı bulur
        /// </summary>
        private static ScrollViewer? GetScrollViewer(DependencyObject o)
        {
            if (o is ScrollViewer sv) return sv;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
            {
                var child = VisualTreeHelper.GetChild(o, i);
                var result = GetScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }
    }
}