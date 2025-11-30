using System;
using System.Threading.Tasks;
using System.Windows;

namespace UniCast.App.Views
{
    /// <summary>
    /// Uygulama açılış ekranı
    /// </summary>
    public partial class SplashWindow : Window
    {
        public bool InitializationSuccess { get; private set; }
        public string? ErrorMessage { get; private set; }

        public SplashWindow()
        {
            InitializeComponent();
            Loaded += SplashWindow_Loaded;
        }

        private async void SplashWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await InitializeApplicationAsync();
                InitializationSuccess = true;
            }
            catch (Exception ex)
            {
                InitializationSuccess = false;
                ErrorMessage = ex.Message;
            }
            finally
            {
                Close();
            }
        }

        private async Task InitializeApplicationAsync()
        {
            // Step 1: Temel kontroller
            UpdateProgress(0, "Sistem kontrolleri yapılıyor...");
            await Task.Delay(300);

            // Step 2: Yapılandırma yükleme
            UpdateProgress(20, "Yapılandırma yükleniyor...");
            await Task.Delay(300);

            // Step 3: Lisans kontrolü
            UpdateProgress(40, "Lisans doğrulanıyor...");
            await Task.Delay(500);

            // Step 4: Servisler başlatılıyor
            UpdateProgress(60, "Servisler başlatılıyor...");
            await Task.Delay(400);

            // Step 5: UI hazırlanıyor
            UpdateProgress(80, "Arayüz hazırlanıyor...");
            await Task.Delay(300);

            // Step 6: Tamamlandı
            UpdateProgress(100, "Hazır!");
            await Task.Delay(200);
        }

        private void UpdateProgress(int value, string status)
        {
            Dispatcher.Invoke(() =>
            {
                if (LoadingBar != null)
                {
                    LoadingBar.Value = value;
                }
                if (StatusText != null)
                {
                    StatusText.Text = status;
                }
            });
        }

        public void SetProgress(int value, string status)
        {
            UpdateProgress(value, status);
        }
    }
}