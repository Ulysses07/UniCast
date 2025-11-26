using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi; // NAudio kütüphanesi
using UniCast.App.Services.Capture;

namespace UniCast.App.Services
{
    public sealed class AudioService : IDisposable
    {
        private MMDevice? _selectedDevice;
        private readonly MMDeviceEnumerator _enumerator;
        private CancellationTokenSource? _cts;

        public event Action<float>? OnLevelChange; // 0.0 ile 1.0 arası ses seviyesi
        public event Action<bool>? OnMuteChange;   // Mute durumu değişince

        public AudioService()
        {
            _enumerator = new MMDeviceEnumerator();
        }

        public async Task InitializeAsync(string deviceId)
        {
            StopMonitoring();

            await Task.Run(() =>
            {
                try
                {
                    // NAudio ile sistemdeki mikrofonları tara
                    var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

                    // ID eşleştirmesi (WinRT ID'si NAudio ID'sini içerir mi?)
                    // Eğer deviceId boşsa varsayılanı al.
                    if (string.IsNullOrEmpty(deviceId))
                    {
                        _selectedDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                    }
                    else
                    {
                        // Eşleşen cihazı bulmaya çalış
                        _selectedDevice = devices.FirstOrDefault(d => deviceId.Contains(d.ID, StringComparison.OrdinalIgnoreCase))
                                          ?? _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                    }

                    StartMonitoring();
                }
                catch
                {
                    // Hata olursa (örn: mikrofon yok) sessizce geç 
                }
            });
        }

        private void StartMonitoring()
        {
            if (_selectedDevice == null) return;

            _cts = new CancellationTokenSource();

            // Mute durumunu ilk başta bildir
            OnMuteChange?.Invoke(_selectedDevice.AudioEndpointVolume.Mute);

            // Arka planda sürekli ses seviyesini oku
            Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        // Anlık ses seviyesi (0.0 - 1.0)
                        var level = _selectedDevice.AudioMeterInformation.MasterPeakValue;
                        OnLevelChange?.Invoke(level);

                        // Mute durumu dışarıdan (Windows ayarlarından) değişirse yakala
                        // (Basitlik için burada event trigger etmiyoruz, sadece UI update için level yeterli)
                    }
                    catch { break; }

                    await Task.Delay(50, _cts.Token); // 20 FPS yenileme hızı
                }
            }, _cts.Token);
        }

        public void ToggleMute()
        {
            if (_selectedDevice != null)
            {
                // Windows üzerinden cihazı sustur (Bu FFmpeg'e giden sesi de keser)
                bool newState = !_selectedDevice.AudioEndpointVolume.Mute;
                _selectedDevice.AudioEndpointVolume.Mute = newState;
                OnMuteChange?.Invoke(newState);
            }
        }

        public void StopMonitoring()
        {
            _cts?.Cancel();
            _selectedDevice = null;
        }

        public void Dispose()
        {
            StopMonitoring();
            _enumerator.Dispose();
        }
    }
}