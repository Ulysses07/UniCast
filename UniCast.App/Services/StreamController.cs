using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Core;
using UniCast.Core.Models;
using UniCast.Core.Settings;
using UniCast.Encoder;

namespace UniCast.App.Services
{
    public interface IStreamController
    {
        Task StartAsync(IEnumerable<TargetItem> targets, SettingsData settings, CancellationToken ct);
        Task StopAsync();
        bool IsRunning { get; }
        string LastMessage { get; }
        string LastMetric { get; }
    }

    public sealed class StreamController : IStreamController
    {
        private CancellationTokenSource? _cts;
        private FfmpegProcess? _ff;

        public bool IsRunning { get; private set; }
        public string LastMessage { get; private set; } = "Idle";
        public string LastMetric { get; private set; } = "";

        public async Task StartAsync(IEnumerable<TargetItem> targets, SettingsData settings, CancellationToken ct)
        {
            if (IsRunning) throw new InvalidOperationException("Stream zaten çalışıyor.");

            // Hızlı doğrulama (net kullanıcı mesajları)
            if (string.IsNullOrWhiteSpace(settings.DefaultCamera))
                throw new InvalidOperationException("Kamera seçilmemiş (Ayarlar > Kamera).");
            if (string.IsNullOrWhiteSpace(settings.DefaultMicrophone))
                throw new InvalidOperationException("Mikrofon seçilmemiş (Ayarlar > Mikrofon).");

            bool anyTarget = false;
            foreach (var t in targets) if (t.Enabled && !string.IsNullOrWhiteSpace(t.Url)) { anyTarget = true; break; }
            if (!anyTarget)
                throw new InvalidOperationException("Aktif ve boş olmayan en az bir RTMP/RTMPS hedef ekleyin (Hedefler sekmesi).");

            var profile = new EncoderProfile
            {
                Width = settings.Width,
                Height = settings.Height,
                Fps = settings.Fps,
                VideoKbps = settings.VideoKbps,
                AudioKbps = settings.AudioKbps,
                Encoder = settings.Encoder,
                GopSeconds = 2
            };

            string? recordFile = null;
            if (settings.EnableLocalRecord)
            {
                Directory.CreateDirectory(settings.RecordFolder);
                recordFile = Path.Combine(settings.RecordFolder, $"unicast_{DateTime.Now:yyyyMMdd_HHmmss}.flv");
            }

            var build = FfmpegArgsBuilder.BuildSingleEncodeMultiRtmp(targets, settings, profile, recordFile);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ff = new FfmpegProcess();
            _ff.OnLog += s => LastMessage = s;
            _ff.OnMetric += s => LastMetric = s;
            _ff.OnExit += code => { IsRunning = false; LastMessage = $"FFmpeg exited {(code is null ? "" : $"(code {code})")}"; };

            await _ff.StartAsync(build.Args, _cts.Token);
            IsRunning = true;
            LastMessage = $"Started: {build.VideoEncoder}/aac → {build.Outputs.Length} hedef";
        }

        public async Task StopAsync()
        {
            if (!IsRunning) return;
            try
            {
                _cts?.Cancel();
                if (_ff is not null) await _ff.StopAsync();
            }
            finally
            {
                IsRunning = false; LastMessage = "Stopped"; LastMetric = "";
            }
        }
    }
}
