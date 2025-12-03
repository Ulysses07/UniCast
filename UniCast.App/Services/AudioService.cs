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
        private Task? _monitorTask;
        private bool _disposed;

        public event Action<float>? OnLevelChange;
        public event Action<bool>? OnMuteChange;

        public AudioService()
        {
            _enumerator = new MMDeviceEnumerator();
        }

        public async Task InitializeAsync(string deviceId)
        {
            // Önce mevcut monitoring'i düzgün durdur
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
                catch (Exception ex)
                {
                    // DÜZELTME v25: Mikrofon yoksa sessizce geç - loglama eklendi
                    System.Diagnostics.Debug.WriteLine($"[AudioService] Mikrofon başlatma hatası: {ex.Message}");
                }
            });
        }

        private void StartMonitoring()
        {
            if (_selectedDevice == null || _disposed) return;

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
                    catch (Exception ex)
                    {
                        // DÜZELTME v27: Exception logging eklendi
                        System.Diagnostics.Debug.WriteLine($"[AudioService] Monitor loop exception: {ex.Message}");
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

        private async Task StopMonitoringAsync()
        {
            if (_cts != null)
            {
                try
                {
                    _cts.Cancel();
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AudioService] CTS cancel hatası: {ex.Message}"); }

                if (_monitorTask != null)
                {
                    try
                    {
                        await _monitorTask.ConfigureAwait(false);
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AudioService] Monitor task bekleme hatası: {ex.Message}"); }
                }

                _cts.Dispose();
                _cts = null;
            }

            _monitorTask = null;
            _selectedDevice = null;
        }

        // DÜZELTME: Senkron versiyonu güvenli hale getirildi
        public void StopMonitoring()
        {
            if (_cts == null) return;

            try
            {
                _cts.Cancel();

                // Task'ın bitmesini kısa süre bekle - deadlock önleme
                _monitorTask?.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AudioService] StopMonitoring hatası: {ex.Message}"); }
            finally
            {
                try { _cts?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AudioService] CTS dispose hatası: {ex.Message}"); }
                _cts = null;
                _monitorTask = null;
                _selectedDevice = null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Event'leri temizle
            OnLevelChange = null;
            OnMuteChange = null;

            // Senkron dispose için güvenli yol
            try
            {
                _cts?.Cancel();
                _monitorTask?.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AudioService] Dispose wait hatası: {ex.Message}"); }

            try { _cts?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AudioService] CTS dispose hatası: {ex.Message}"); }
            _cts = null;
            _monitorTask = null;
            _selectedDevice = null;

            try
            {
                _enumerator.Dispose();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AudioService] Enumerator dispose hatası: {ex.Message}"); }
        }
    }
}