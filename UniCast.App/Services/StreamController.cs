using System.Linq;
using System.Threading.Tasks;
using UniCast.App.ViewModels;

namespace UniCast.App.Services
{
    /// <summary>
    /// UI ile ViewModel arasında basit orkestrasyon.
    /// Eski sürümdeki AttachEncoder / Status dışarıdan set vb. artık yok.
    /// </summary>
    public sealed class StreamController
    {
        private readonly ControlViewModel _controlVm;
        private readonly TargetsViewModel _targetsVm;

        public StreamController(ControlViewModel controlVm, TargetsViewModel targetsVm)
        {
            _controlVm = controlVm;
            _targetsVm = targetsVm;
        }

        public async Task StartAsync()
        {
            var urls = _targetsVm.GetEnabledUrls().ToList();
            await _controlVm.StartAsync(urls);
        }

        public Task StopAsync() => _controlVm.StopAsync();
    }
}
