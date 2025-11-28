using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using UniCast.App.Services.Capture;

namespace UniCast.App.Services
{
    public sealed class AudioService : IDisposable
    {
        private MMDevice? _selectedDevice;
        private readonly MMDeviceEnumerator _enumerator;
        private CancellationTokenSource? _cts;
        private Task? _monitorTask;  // DÜZELTME: Task'ı takip et
        private bool _disposed;      // DÜZELTME: Dispose flag

        public event Action<float>? OnLevelChange;
        public event Action<bool>? OnMuteChange;

        public AudioService()
        {
            _enumerator = new MMDeviceEnumerator();
        }

        public async Task InitializeAsync(string deviceId)
        {
            // DÜZELTME: Önce mevcut monitoring'i düzgün durdur
            await StopMonitoringAsync();

            await Task.Run(() =>
            {
                try
                {
                    var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

                    if (string.IsNullOrEmpty(deviceId))
                    {
                        _selectedDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                    }
                    else
                    {
                        _selectedDevice = devices.FirstOrDefault(d => deviceId.Contains(d.ID, StringComparison.OrdinalIgnoreCase))
                                          ?? _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                    }

                    StartMonitoring();
                }
                catch
                {
                    // Mikrofon yoksa sessizce geç
                }
            });
        }

        private void StartMonitoring()
        {
            if (_selectedDevice == null || _disposed) return;

            // DÜZELTME: Eski CTS'i dispose et
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            OnMuteChange?.Invoke(_selectedDevice.AudioEndpointVolume.Mute);

            var token = _cts.Token;
            _monitorTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (_selectedDevice == null) break;

                        var level = _selectedDevice.AudioMeterInformation.MasterPeakValue;
                        OnLevelChange?.Invoke(level);
                    }
                    catch
                    {
                        break;
                    }

                    try
                    {
                        await Task.Delay(50, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, token);
        }

        public void ToggleMute()
        {
            if (_selectedDevice != null && !_disposed)
            {
                bool newState = !_selectedDevice.AudioEndpointVolume.Mute;
                _selectedDevice.AudioEndpointVolume.Mute = newState;
                OnMuteChange?.Invoke(newState);
            }
        }

        // DÜZELTME: Async stop metodu eklendi
        private async Task StopMonitoringAsync()
        {
            if (_cts != null)
            {
                try
                {
                    _cts.Cancel();
                }
                catch { }

                // Task'ın bitmesini bekle
                if (_monitorTask != null)
                {
                    try
                    {
                        await _monitorTask.ConfigureAwait(false);
                    }
                    catch { }
                }

                // DÜZELTME: CTS dispose ediliyordu mu? Şimdi ediliyor!
                _cts.Dispose();
                _cts = null;
            }

            _monitorTask = null;
            _selectedDevice = null;
        }

        public void StopMonitoring()
        {
            // Senkron wrapper (geriye uyumluluk için)
            StopMonitoringAsync().GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Event'leri temizle
            OnLevelChange = null;
            OnMuteChange = null;

            StopMonitoring();

            try
            {
                _enumerator.Dispose();
            }
            catch { }
        }
    }
}