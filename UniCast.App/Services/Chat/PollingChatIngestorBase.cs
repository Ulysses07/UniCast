using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Core.Chat;

namespace UniCast.App.Services.Chat
{
    /// <summary>
    /// Tüm polling (sorgulama) tabanlı chat servisleri için ortak temel sınıf.
    /// Başlatma, durdurma, hata yönetimi ve akıllı bekleme (backoff) mantığını tek merkezde toplar.
    /// </summary>
    public abstract class PollingChatIngestorBase : IChatIngestor
    {
        private CancellationTokenSource? _cts;
        private Task? _runner;

        // IChatIngestor Implementasyonu
        public event Action<ChatMessage>? OnMessage;
        public abstract string Name { get; }
        public bool IsRunning { get; private set; }

        public async Task StartAsync(CancellationToken ct)
        {
            if (IsRunning) return;

            // Önce ayar kontrolü (Türeyen sınıf bunu implemente eder)
            ValidateSettings();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            IsRunning = true;

            // Arka planda döngüyü başlat
            _runner = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
            await Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (!IsRunning) return;
            try { _cts?.Cancel(); } catch { }
            if (_runner is not null)
            {
                try { await _runner.ConfigureAwait(false); } catch { }
            }
            IsRunning = false;
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        // --- SOYUT METOTLAR (Çocuk sınıflar bunları dolduracak) ---

        /// <summary>
        /// Başlamadan önce ayarların geçerli olup olmadığını kontrol et.
        /// Geçersizse Exception fırlat.
        /// </summary>
        protected abstract void ValidateSettings();

        /// <summary>
        /// Döngü başlamadan önceki hazırlık (Örn: YouTube Video ID bulma).
        /// </summary>
        protected abstract Task InitializeAsync(CancellationToken ct);

        /// <summary>
        /// Tek bir veri çekme işlemi. Mesajları ve bir sonraki bekleme süresini (opsiyonel) döner.
        /// </summary>
        protected abstract Task<(IEnumerable<ChatMessage> messages, int? nextDelayMs)> FetchMessagesAsync(CancellationToken ct);

        // --- ORTAK DÖNGÜ MANTIĞI ---

        private async Task RunLoopAsync(CancellationToken ct)
        {
            int errorCount = 0;
            var backoffMs = 1000;

            try
            {
                // 1. Hazırlık Aşaması
                try
                {
                    await InitializeAsync(ct);
                }
                catch (Exception)
                {
                    // Hazırlık aşamasında hata olursa (örn: yayın bulunamadı) loglayıp çıkabiliriz
                    // System.Diagnostics.Debug.WriteLine($"[{Name}] Init Error: {ex.Message}");
                    return;
                }

                // 2. Ana Döngü
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        // Veri Çek
                        var (messages, explicitDelay) = await FetchMessagesAsync(ct);

                        // Başarılı oldu, hata sayacını sıfırla
                        errorCount = 0;
                        int messageCount = 0;

                        if (messages != null)
                        {
                            foreach (var msg in messages)
                            {
                                OnMessage?.Invoke(msg);
                                messageCount++;
                            }
                        }

                        // Bekleme Süresini Belirle
                        if (explicitDelay.HasValue && explicitDelay.Value > 0)
                        {
                            // Platformun emrettiği süre (örn: YouTube pollingIntervalMillis)
                            backoffMs = explicitDelay.Value;
                        }
                        else
                        {
                            // Akıllı Bekleme: Mesaj yoksa yavaşla, varsa hızlan
                            if (messageCount == 0)
                                backoffMs = Math.Min(backoffMs + 500, 5000); // Max 5sn
                            else
                                backoffMs = 1000; // Mesaj varsa 1sn
                        }

                        await Task.Delay(backoffMs, ct);
                    }
                    catch (HttpRequestException) // İnternet/API Hatası
                    {
                        errorCount++;
                        var retryDelay = Math.Min(2000 * errorCount, 30000); // Exponential Backoff
                        await Task.Delay(retryDelay, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Beklenmedik hata (Parse hatası vb.)
                        await Task.Delay(5000, ct);
                    }
                }
            }
            catch (OperationCanceledException) { /* Normal duruş */ }
            finally
            {
                IsRunning = false;
            }
        }
    }
}