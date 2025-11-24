using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UniCast.Core.Chat;

namespace UniCast.App.Services.Chat
{
    public abstract class PollingChatIngestorBase : IChatIngestor
    {
        private CancellationTokenSource? _cts;
        private Task? _runner;

        public event Action<ChatMessage>? OnMessage;
        public abstract string Name { get; }
        public bool IsRunning { get; private set; }

        public async Task StartAsync(CancellationToken ct)
        {
            if (IsRunning) return;

            ValidateSettings();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            IsRunning = true;

            _runner = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
            await Task.CompletedTask;
        }

        // DÜZELTME BURADA: "virtual" anahtar kelimesi eklendi.
        // Artık TikTokChatIngestor bu metodu override edebilir.
        public virtual async Task StopAsync()
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

        protected abstract void ValidateSettings();
        protected abstract Task InitializeAsync(CancellationToken ct);
        protected abstract Task<(IEnumerable<ChatMessage> messages, int? nextDelayMs)> FetchMessagesAsync(CancellationToken ct);

        private async Task RunLoopAsync(CancellationToken ct)
        {
            int errorCount = 0;
            var backoffMs = 1000;

            try
            {
                try
                {
                    await InitializeAsync(ct);
                }
                catch (Exception)
                {
                    // Init hatası (loglanabilir), döngüye girmeden çık
                    return;
                }

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var (messages, explicitDelay) = await FetchMessagesAsync(ct);

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

                        if (explicitDelay.HasValue && explicitDelay.Value > 0)
                            backoffMs = explicitDelay.Value;
                        else
                        {
                            if (messageCount == 0)
                                backoffMs = Math.Min(backoffMs + 500, 5000);
                            else
                                backoffMs = 1000;
                        }

                        await Task.Delay(backoffMs, ct);
                    }
                    catch (HttpRequestException)
                    {
                        errorCount++;
                        var retryDelay = Math.Min(2000 * errorCount, 30000);
                        await Task.Delay(retryDelay, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await Task.Delay(5000, ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                IsRunning = false;
            }
        }
    }
}