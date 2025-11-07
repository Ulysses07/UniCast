using System.Windows; // WPF Application
using UniCast.App.Services.Capture;

namespace UniCast.App
{
    public partial class App : System.Windows.Application
    {
        public static IDeviceService DeviceService { get; private set; } = new DeviceService();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DeviceService = new MediaFoundationCaptureService();
        }
    }
}
