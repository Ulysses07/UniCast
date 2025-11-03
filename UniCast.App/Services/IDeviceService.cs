namespace UniCast.App.Services
{
    public interface IDeviceService
    {
        /// <summary>
        /// Video ve ses giriş cihazlarını İSİM olarak döndürür (DirectShow).
        /// </summary>
        (IEnumerable<string> video, IEnumerable<string> audio) ListDevices();
    }
}
