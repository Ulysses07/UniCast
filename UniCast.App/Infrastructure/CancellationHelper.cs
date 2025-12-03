using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.App.Infrastructure
{
    /// <summary>
    /// DÜZELTME v20: Gelişmiş CancellationToken yönetimi
    /// Timeout, linked tokens, graceful cancellation
    /// </summary>
    public static class CancellationHelper
    {
        #region Timeout Extensions

        /// <summary>
        /// Task'a timeout ekle
        /// </summary>
        public static async Task<T> WithTimeout<T>(
            this Task<T> task,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                return await task.WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds:F1} seconds");
            }
        }

        /// <summary>
        /// Task'a timeout ekle (void)
        /// </summary>
        public static async Task WithTimeout(
            this Task task,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                await task.WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds:F1} seconds");
            }
        }

        /// <summary>
        /// Timeout ile çalıştır
        /// </summary>
        public static async Task<T> ExecuteWithTimeout<T>(
            Func<CancellationToken, Task<T>> action,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                return await action(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds:F1} seconds");
            }
        }

        #endregion

        #region Linked Tokens

        /// <summary>
        /// Birden fazla CancellationToken'ı birleştir
        /// </summary>
        public static CancellationTokenSource CreateLinkedSource(params CancellationToken[] tokens)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(tokens);
        }

        /// <summary>
        /// Timeout'lu linked token oluştur
        /// </summary>
        public static CancellationTokenSource CreateLinkedSourceWithTimeout(
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            var timeoutCts = new CancellationTokenSource(timeout);

            if (ct == default)
                return timeoutCts;

            return CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        }

        #endregion

        #region Safe Cancellation

        /// <summary>
        /// Güvenli iptal kontrolü - exception fırlatmaz
        /// </summary>
        public static bool IsCancellationRequested(this CancellationToken ct)
        {
            try
            {
                return ct.IsCancellationRequested;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Güvenli ThrowIfCancellationRequested - log ile
        /// </summary>
        public static void ThrowIfCancelledWithLog(
            this CancellationToken ct,
            string operationName)
        {
            if (ct.IsCancellationRequested)
            {
                Log.Debug("[Cancellation] {Operation} iptal edildi", operationName);
                ct.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Cancellation'ı handle et, exception'ı yut
        /// </summary>
        public static async Task<T?> HandleCancellation<T>(
            Func<CancellationToken, Task<T>> action,
            CancellationToken ct,
            T? defaultValue = default)
        {
            try
            {
                return await action(ct);
            }
            catch (OperationCanceledException)
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Cancellation'ı handle et (void)
        /// </summary>
        public static async Task HandleCancellation(
            Func<CancellationToken, Task> action,
            CancellationToken ct)
        {
            try
            {
                await action(ct);
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation
            }
        }

        #endregion

        #region Graceful Shutdown

        /// <summary>
        /// Graceful shutdown için CancellationTokenSource
        /// </summary>
        public static GracefulShutdownToken CreateGracefulShutdownToken(TimeSpan gracePeriod)
        {
            return new GracefulShutdownToken(gracePeriod);
        }

        #endregion

        #region Wait Extensions

        /// <summary>
        /// Cancellation destekli delay
        /// </summary>
        public static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
        }

        /// <summary>
        /// Belirli bir koşul sağlanana kadar bekle
        /// </summary>
        public static async Task<bool> WaitUntilAsync(
            Func<bool> condition,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken ct = default)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                while (!condition())
                {
                    linkedCts.Token.ThrowIfCancellationRequested();
                    await Task.Delay(pollInterval, linkedCts.Token);
                }
                return true;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                return false;
            }
        }

        #endregion

        #region Progress Reporting

        /// <summary>
        /// İptal edilebilir progress raporlama
        /// </summary>
        public static IProgress<T> CreateCancellableProgress<T>(
            Action<T> handler,
            CancellationToken ct)
        {
            return new CancellableProgress<T>(handler, ct);
        }

        private class CancellableProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;
            private readonly CancellationToken _ct;

            public CancellableProgress(Action<T> handler, CancellationToken ct)
            {
                _handler = handler;
                _ct = ct;
            }

            public void Report(T value)
            {
                if (!_ct.IsCancellationRequested)
                {
                    _handler(value);
                }
            }
        }

        #endregion
    }

    #region GracefulShutdownToken

    /// <summary>
    /// Graceful shutdown için özel token
    /// İlk sinyal: yumuşak kapatma başlat
    /// Grace period sonunda: zorla kapat
    /// </summary>
    public sealed class GracefulShutdownToken : IDisposable
    {
        private readonly CancellationTokenSource _softCts = new();
        private readonly CancellationTokenSource _hardCts = new();
        private readonly TimeSpan _gracePeriod;
        private bool _shutdownRequested;
        private bool _disposed;

        public CancellationToken SoftToken => _softCts.Token;
        public CancellationToken HardToken => _hardCts.Token;
        public bool IsShutdownRequested => _shutdownRequested;

        public GracefulShutdownToken(TimeSpan gracePeriod)
        {
            _gracePeriod = gracePeriod;
        }

        /// <summary>
        /// Graceful shutdown başlat
        /// </summary>
        public async Task RequestShutdownAsync()
        {
            if (_shutdownRequested) return;
            _shutdownRequested = true;

            Log.Information("[GracefulShutdown] Yumuşak kapatma başlatıldı. Grace period: {GracePeriod}", _gracePeriod);

            // Soft cancellation
            _softCts.Cancel();

            // Grace period bekle
            await Task.Delay(_gracePeriod);

            // Hard cancellation
            Log.Warning("[GracefulShutdown] Grace period doldu, zorla kapatılıyor");
            _hardCts.Cancel();
        }

        /// <summary>
        /// Hemen kapat
        /// </summary>
        public void ForceShutdown()
        {
            _shutdownRequested = true;
            _softCts.Cancel();
            _hardCts.Cancel();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _softCts.Dispose();
            _hardCts.Dispose();
        }
    }

    #endregion
}
