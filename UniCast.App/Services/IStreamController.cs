using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Core.Core;
using UniCast.Core.Models;
using UniCast.Core.Streaming;

// App.Services.SettingsData kullanılacak
using SettingsData = UniCast.App.Services.SettingsData;

namespace UniCast.App.Services
{
    public interface IStreamController : IAsyncDisposable
    {
        bool IsRunning { get; }
        bool IsReconnecting { get; }
        Profile CurrentProfile { get; }

        IReadOnlyList<StreamTarget> Targets { get; }

        // --- PIPE INPUT DESTEĞİ ---
        /// <summary>
        /// Video input olarak named pipe kullan (PreviewService'den)
        /// </summary>
        void SetPipeInput(string pipeName, int width, int height, int fps);
        
        /// <summary>
        /// Pipe input'u devre dışı bırak (kamera direkt kullanılır)
        /// </summary>
        void ClearPipeInput();

        // --- YENİ EKLENEN METOTLAR (StartWithResultAsync) ---
        // UI tarafı artık bunları kullanarak "Başarılı/Başarısız" bilgisini alacak.

        // 1) En detaylı başlangıç (ViewModel genelde bunu kullanır)
        Task<StreamStartResult> StartWithResultAsync(IEnumerable<TargetItem> targets, SettingsData settings, CancellationToken ct);

        // 2) Manuel profil ile başlangıç
        Task<StreamStartResult> StartWithResultAsync(Profile profile, CancellationToken ct = default);

        // --- ESKİ METOTLAR (Legacy Support) ---
        Task StartAsync(Profile profile, CancellationToken ct = default);
        Task StartAsync(Profile profile, IEnumerable<StreamTarget> targets, CancellationToken ct);
        Task StartAsync(IEnumerable<TargetItem> targets, SettingsData settings, CancellationToken ct);
        Task StopAsync(CancellationToken ct = default);

        void AddTarget(StreamTarget target);
        void RemoveTarget(StreamTarget target);

        string? LastAdvisory { get; }
        string? LastMessage { get; }
        string? LastMetric { get; }

        event EventHandler<string>? OnLog;
        event EventHandler<StreamMetric>? OnMetric;
        event EventHandler<int>? OnExit;
    }
}