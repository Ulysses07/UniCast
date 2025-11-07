using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using UniCast.App.Infrastructure;
using UniCast.App.Services;
using UniCast.Core.Models;
using UniCast.Core.Settings;
using System.Collections.ObjectModel;

namespace UniCast.App.ViewModels
{
    public sealed class ControlViewModel : INotifyPropertyChanged
    {
        private readonly IStreamController _stream;
        private readonly Func<(ObservableCollection<TargetItem> targets, SettingsData settings)> _provider;
        private readonly PreviewService _preview = new();
        private CancellationTokenSource? _cts;

        public ControlViewModel(
            IStreamController stream,
            Func<(ObservableCollection<TargetItem> targets, SettingsData settings)> provider)
        {
            _stream = stream;
            _provider = provider;

            _preview.OnFrame += bmp => PreviewImage = bmp;

            StartCommand = new RelayCommand(async _ => await StartAsync(), _ => !IsRunning);
            StopCommand = new RelayCommand(async _ => await StopAsync(), _ => IsRunning);
        }

        // --- Preview ---
        private ImageSource? _previewImage;
        public ImageSource? PreviewImage
        {
            get => _previewImage;
            private set { _previewImage = value; OnPropertyChanged(); }
        }

        public async Task StartPreviewAsync()
        {
            var s = Services.SettingsStore.Load();

            if (!_preview.IsRunning)
            {
                // preferredIndex: bilmiyorsak -1
                await _preview.StartAsync(-1, s.Width, s.Height, s.Fps);
            }
        }

        public Task StopPreviewAsync() => _preview.StopAsync();

        // --- Stream state ---
        private string _status = "Idle";
        public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }

        private string _metric = "";
        public string Metric { get => _metric; private set { _metric = value; OnPropertyChanged(); } }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                _isRunning = value;
                OnPropertyChanged();
                (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        // --- Advisory (Encoder seçimi + platform limitleri) ---
        private string _advisory = "";
        public string Advisory
        {
            get => _advisory;
            private set { _advisory = value; OnPropertyChanged(); }
        }

        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }

        private async Task StartAsync()
        {
            try
            {
                var (targets, settings) = _provider();
                _cts = new CancellationTokenSource();

                Status = "Starting…";
                Metric = "";
                Advisory = "";

                await _stream.StartAsync(targets, settings, _cts.Token);
                IsRunning = true;

                // Advisory'yi UI'ya yansıt
                Advisory = _stream.LastAdvisory;

                // Background metric/status updater
                _ = Task.Run(async () =>
                {
                    while (IsRunning || _stream.IsReconnecting)
                    {
                        Status = _stream.LastMessage;
                        Metric = _stream.LastMetric;
                        await Task.Delay(200);
                    }
                });
            }
            catch (Exception ex)
            {
                Status = "Start error: " + ex.Message;
                Advisory = "";
                IsRunning = false;
            }
        }

        private async Task StopAsync()
        {
            try
            {
                Status = "Stopping…";
                _cts?.Cancel();
                await _stream.StopAsync();
            }
            catch (Exception ex)
            {
                Status = "Stop error: " + ex.Message;
            }
            finally
            {
                IsRunning = false;
                Status = "Stopped";
                Metric = "";
                Advisory = "";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
