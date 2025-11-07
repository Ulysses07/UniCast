using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Core.Models;
using UniCast.Core.Settings; // SettingsData için

namespace UniCast.App.Services
{
    public interface IStreamController : IAsyncDisposable
    {
        bool IsRunning { get; }
        bool IsReconnecting { get; }
        Profile CurrentProfile { get; }

        IReadOnlyList<StreamTarget> Targets { get; }

        // 1) Klasik başlangıç
        Task StartAsync(Profile profile, CancellationToken ct = default);

        // 2) Dışarıdan hedef ver
        Task StartAsync(Profile profile, IEnumerable<StreamTarget> targets, CancellationToken ct);

        // 3) Eski VM çağrısı (TargetItem + SettingsData)
        Task StartAsync(IEnumerable<TargetItem> targets, SettingsData settings, CancellationToken ct);

        Task StopAsync(CancellationToken ct = default);

        void AddTarget(StreamTarget target);
        void RemoveTarget(StreamTarget target);

        // VM'in bizzat okuduğu son durumlar
        string? LastAdvisory { get; }
        string? LastMessage { get; }
        string? LastMetric { get; } // string olarak

        event EventHandler<string>? OnLog;
        event EventHandler<StreamMetric>? OnMetric; // UniCast.Core.Models.StreamMetric
        event EventHandler<int /*exitCode*/>? OnExit;
    }
}
