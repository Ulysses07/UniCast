using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Core;
using UniCast.Encoder;

namespace UniCast.App.ViewModels
{
    public sealed class ControlViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly IEncoderService _encoder;

        private string _status = "Idle";
        private double _fps;
        private double _videoKbps;

        public ControlViewModel(IEncoderService encoder)
        {
            _encoder = encoder;
            // Encoder metriklerini üstten dinleyip property’lere akıt
            _encoder.OnMetrics += m =>
            {
                Fps = m.Fps;
                VideoKbps = m.VideoKbps;
            };
        }

        // UI’nin seçtiği profil (varsayılan 720p30 / 3500 / 128)
        public EncoderProfile Preset { get; set; } = new EncoderProfile(
            "720p30", // name
            1280,     // width
            720,      // height
            30,       // fps
            3500,     // videoKbps
            128       // audioKbps
        );

        public string? SelectedCamera { get; set; }
        public string? SelectedMic { get; set; }

        public string Status
        {
            get => _status;
            private set { _status = value; OnPropertyChanged(); }
        }

        public double Fps
        {
            get => _fps;
            private set { _fps = value; OnPropertyChanged(); }
        }

        public double VideoKbps
        {
            get => _videoKbps;
            private set { _videoKbps = value; OnPropertyChanged(); }
        }

        public async Task StartAsync(IEnumerable<string> urls)
        {
            var list = urls?.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).ToList() ?? new();
            if (list.Count == 0)
                throw new InvalidOperationException("En az bir hedef URL girin.");

            Status = "Starting…";
            await _encoder.StartAsync(Preset, list, SelectedCamera, SelectedMic, CancellationToken.None);
            Status = "Streaming";
        }

        public async Task StopAsync()
        {
            Status = "Stopping…";
            await _encoder.StopAsync(CancellationToken.None);
            Status = "Stopped";
        }

        public void Dispose()
        {
            try { _encoder?.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
