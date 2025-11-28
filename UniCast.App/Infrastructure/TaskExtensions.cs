using System;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.App.Infrastructure
{
    /// <summary>
    /// Fire-and-forget async çağrıları için güvenli wrapper.
    /// Exception'ları yutar ama loglar.
    /// </summary>
    public static class TaskExtensions
    {
        /// <summary>
        /// Task'ı fire-and-forget olarak çalıştırır, hataları loglar.
        /// </summary>
        /// <param name="task">Çalıştırılacak task</param>
        /// <param name="context">Hata mesajında gösterilecek bağlam (örn: "YouTube Chat")</param>
        /// <param name="onError">Opsiyonel hata callback'i</param>
        public static void SafeFireAndForget(
            this Task task,
            string context = "Background Task",
            Action<Exception>? onError = null)
        {
            if (task == null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // İptal normal, loglama
                    Log.Debug("[{Context}] İptal edildi", context);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[{Context}] Fire-and-forget hatası", context);

                    try
                    {
                        onError?.Invoke(ex);
                    }
                    catch (Exception callbackEx)
                    {
                        Log.Error(callbackEx, "[{Context}] Hata callback'i başarısız", context);
                    }
                }
            });
        }

        /// <summary>
        /// Task'ı fire-and-forget olarak çalıştırır, belirli exception türlerini yok sayar.
        /// </summary>
        public static void SafeFireAndForget<TException>(
            this Task task,
            string context = "Background Task")
            where TException : Exception
        {
            if (task == null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Normal
                }
                catch (TException)
                {
                    // Belirtilen exception türü yok sayılıyor
                    Log.Debug("[{Context}] Beklenen hata yok sayıldı: {ExceptionType}",
                        context, typeof(TException).Name);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[{Context}] Fire-and-forget hatası", context);
                }
            });
        }

        /// <summary>
        /// Async işlemi timeout ile çalıştırır.
        /// </summary>
        public static async Task<T> WithTimeout<T>(
            this Task<T> task,
            TimeSpan timeout,
            string context = "Async Operation")
        {
            using var cts = new System.Threading.CancellationTokenSource();
            var delayTask = Task.Delay(timeout, cts.Token);

            var completedTask = await Task.WhenAny(task, delayTask);

            if (completedTask == delayTask)
            {
                Log.Warning("[{Context}] Timeout ({Timeout}ms)", context, timeout.TotalMilliseconds);
                throw new TimeoutException($"{context}: {timeout.TotalMilliseconds}ms timeout");
            }

            cts.Cancel(); // Delay task'ı iptal et
            return await task;
        }

        /// <summary>
        /// Async işlemi timeout ile çalıştırır (void dönüş).
        /// </summary>
        public static async Task WithTimeout(
            this Task task,
            TimeSpan timeout,
            string context = "Async Operation")
        {
            using var cts = new System.Threading.CancellationTokenSource();
            var delayTask = Task.Delay(timeout, cts.Token);

            var completedTask = await Task.WhenAny(task, delayTask);

            if (completedTask == delayTask)
            {
                Log.Warning("[{Context}] Timeout ({Timeout}ms)", context, timeout.TotalMilliseconds);
                throw new TimeoutException($"{context}: {timeout.TotalMilliseconds}ms timeout");
            }

            cts.Cancel();
            await task;
        }
    }
}