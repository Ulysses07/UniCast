using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        bool IsReconnecting { get; }
        string LastMessage { get; }
        string LastMetric { get; }
        string LastAdvisory { get; }
    }

    public sealed class StreamController : IStreamController
    {
        private CancellationTokenSource? _cts;
        private FfmpegProcess? _ff;

        public bool IsRunning { get; private set; }
        public bool IsReconnecting { get; private set; }
        public string LastMessage { get; private set; } = "Idle";
        public string LastMetric { get; private set; } = "";
        public string LastAdvisory { get; private set; } = "";

        private int _reconnectAttempts = 0;
        private const int MaxReconnectAttempts = 3;
        private const int ReconnectDelayMs = 3000;

        public async Task StartAsync(IEnumerable<TargetItem> targets, SettingsData settings, CancellationToken externalCt)
        {
            if (IsRunning && !IsReconnecting)
                throw new InvalidOperationException("Stream already running.");

            IsRunning = true;
            IsReconnecting = false;
            _reconnectAttempts = 0;

            await RunFfmpeg(targets, settings, externalCt);
        }

        private async Task RunFfmpeg(IEnumerable<TargetItem> targets, SettingsData settings, CancellationToken externalCt)
        {
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

            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string? recordFile = settings.EnableLocalRecord
                ? Path.Combine(settings.RecordFolder, $"unicast_{ts}.mp4")
                : null;

            string? recordFileMkv = settings.EnableLocalRecord
                ? Path.Combine(settings.RecordFolder, $"unicast_{ts}.mkv")
                : null;

            if (recordFile != null) Directory.CreateDirectory(settings.RecordFolder);

            var build = FfmpegArgsBuilder.BuildSingleEncodeMultiRtmp(targets, settings, profile, recordFile);
            LastAdvisory = build.Advisory;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);

            _ff = new FfmpegProcess();
            _ff.OnLog += s => LastMessage = s;
            _ff.OnMetric += s => LastMetric = s;

            _ff.OnExit += async code =>
            {
                IsRunning = false;

                // Kullanıcı STOP ettiyse kayıt onarımı çalışmaz
                if (!(_cts?.IsCancellationRequested ?? false))
                {
                    // --- MP4 SALVAGE LOGIC ---
                    if (settings.EnableLocalRecord && recordFile != null && File.Exists(recordFile))
                    {
                        var info = new FileInfo(recordFile);
                        if (info.Length < 200_000) // 200 KB'dan küçükse bozuk kabul
                        {
                            try
                            {
                                string fixedFile = recordFile.Replace(".mp4", ".fixed.mp4");
                                var ff = FfmpegProcess.ResolveFfmpegPath();

                                var psi = new ProcessStartInfo
                                {
                                    FileName = ff,
                                    Arguments = $"-err_detect ignore_err -i \"{recordFile}\" -c copy \"{fixedFile}\" -y",
                                    CreateNoWindow = true,
                                    UseShellExecute = false
                                };
                                Process.Start(psi)?.WaitForExit();

                                if (File.Exists(fixedFile) && new FileInfo(fixedFile).Length > 200_000)
                                {
                                    File.Delete(recordFile);
                                    File.Move(fixedFile, recordFile);
                                    LastMessage = "Kayıt dosyası kurtarıldı.";
                                }
                                else
                                {
                                    if (recordFileMkv != null && File.Exists(recordFileMkv))
                                    {
                                        File.Delete(recordFile);
                                        File.Move(recordFileMkv, recordFile);
                                        LastMessage = "MP4 bozuldu → MKV'e döndürüldü.";
                                    }
                                }
                            }
                            catch { /* Sessiz geç */ }
                        }
                    }
                }

                // Kullanıcı stop ettiyse reconnect etme
                if (_cts?.IsCancellationRequested ?? false) return;

                // --- AUTO RECONNECT ---
                if (_reconnectAttempts < MaxReconnectAttempts)
                {
                    IsReconnecting = true;
                    _reconnectAttempts++;

                    LastMessage = $"Bağlantı koptu — Yeniden bağlanılıyor {_reconnectAttempts}/{MaxReconnectAttempts}...";
                    await Task.Delay(ReconnectDelayMs);

                    await RunFfmpeg(targets, settings, externalCt);
                }
                else
                {
                    IsReconnecting = false;
                    LastMessage = "Yayın koptu ve yeniden bağlanılamadı. Lütfen internet ve RTMP ayarlarını kontrol edin.";
                }
            };

            await _ff.StartAsync(build.Args, _cts.Token);
            LastMessage = "Yayın başladı";
        }

        public async Task StopAsync()
        {
            if (!IsRunning && !IsReconnecting) return;

            IsRunning = false;
            IsReconnecting = false;

            try
            {
                _cts?.Cancel();
                if (_ff is not null)
                    await _ff.StopAsync();
            }
            finally
            {
                LastMessage = "Yayın durduruldu";
                LastMetric = "";
            }
        }
    }
}
