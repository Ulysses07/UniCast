using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace UniCast.App.Infrastructure
{
    /// <summary>
    /// DÜZELTME v18: Async event handler'lar için güvenli wrapper
    /// WPF event handler'ları async void olmak zorunda, bu sınıf hata yakalamayı sağlar
    /// </summary>
    public static class AsyncEventHandler
    {
        /// <summary>
        /// Async event handler'ı güvenli şekilde çalıştır
        /// </summary>
        /// <param name="asyncAction">Async işlem</param>
        /// <param name="onError">Hata durumunda çağrılacak (opsiyonel)</param>
        /// <param name="showErrorDialog">Hata dialogu gösterilsin mi</param>
        /// <param name="callerName">Çağıran metod adı (otomatik)</param>
        public static async void RunSafe(
            Func<Task> asyncAction,
            Action<Exception>? onError = null,
            bool showErrorDialog = false,
            [CallerMemberName] string? callerName = null)
        {
            try
            {
                await asyncAction();
            }
            catch (OperationCanceledException)
            {
                // İptal normal
                Log.Debug("[{Caller}] İşlem iptal edildi", callerName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[{Caller}] Async event handler hatası", callerName);

                // Özel hata handler'ı
                try
                {
                    onError?.Invoke(ex);
                }
                catch (Exception handlerEx)
                {
                    Log.Error(handlerEx, "[{Caller}] Hata handler'ı başarısız", callerName);
                }

                // Dialog göster
                if (showErrorDialog)
                {
                    ShowErrorDialog(ex, callerName);
                }
            }
        }

        /// <summary>
        /// Async event handler'ı güvenli şekilde çalıştır (parametre ile)
        /// </summary>
        public static async void RunSafe<T>(
            Func<T, Task> asyncAction,
            T parameter,
            Action<Exception>? onError = null,
            bool showErrorDialog = false,
            [CallerMemberName] string? callerName = null)
        {
            try
            {
                await asyncAction(parameter);
            }
            catch (OperationCanceledException)
            {
                Log.Debug("[{Caller}] İşlem iptal edildi", callerName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[{Caller}] Async event handler hatası", callerName);

                try
                {
                    onError?.Invoke(ex);
                }
                catch (Exception handlerEx)
                {
                    Log.Error(handlerEx, "[{Caller}] Hata handler'ı başarısız", callerName);
                }

                if (showErrorDialog)
                {
                    ShowErrorDialog(ex, callerName);
                }
            }
        }

        /// <summary>
        /// Fire-and-forget task'ı güvenli şekilde çalıştır
        /// </summary>
        public static void FireAndForget(
            Task task,
            Action<Exception>? onError = null,
            [CallerMemberName] string? callerName = null)
        {
            task.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    var ex = t.Exception.GetBaseException();
                    Log.Error(ex, "[{Caller}] Fire-and-forget task hatası", callerName);

                    try
                    {
                        onError?.Invoke(ex);
                    }
                    catch { }
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>
        /// Sync event handler'dan async kod çalıştır
        /// </summary>
        public static void InvokeAsync(
            Func<Task> asyncAction,
            [CallerMemberName] string? callerName = null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await asyncAction();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[{Caller}] InvokeAsync hatası", callerName);
                }
            });
        }

        /// <summary>
        /// UI thread'de async kod çalıştır
        /// </summary>
        public static void InvokeOnUI(
            Func<Task> asyncAction,
            [CallerMemberName] string? callerName = null)
        {
            Application.Current?.Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await asyncAction();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[{Caller}] InvokeOnUI hatası", callerName);
                }
            });
        }

        #region Error Dialog

        private static void ShowErrorDialog(Exception ex, string? callerName)
        {
            try
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    var message = ex switch
                    {
                        TaskCanceledException => "İşlem iptal edildi.",
                        TimeoutException => "İşlem zaman aşımına uğradı.",
                        UnauthorizedAccessException => "Erişim reddedildi.",
                        InvalidOperationException => $"Geçersiz işlem: {ex.Message}",
                        _ => $"Bir hata oluştu: {ex.Message}"
                    };

                    MessageBox.Show(
                        message,
                        "Hata",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                });
            }
            catch
            {
                // Dialog gösterilemedi
            }
        }

        #endregion
    }

    /// <summary>
    /// DÜZELTME v18: Retry logic helper
    /// </summary>
    public static class RetryHelper
    {
        /// <summary>
        /// Exponential backoff ile retry
        /// </summary>
        public static async Task<T> ExecuteWithRetryAsync<T>(
            Func<Task<T>> action,
            int maxRetries = 3,
            int initialDelayMs = 100,
            int maxDelayMs = 5000,
            Func<Exception, bool>? shouldRetry = null,
            [CallerMemberName] string? callerName = null)
        {
            var delay = initialDelayMs;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    return await action();
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    // Retry yapılmalı mı kontrol et
                    if (shouldRetry != null && !shouldRetry(ex))
                    {
                        throw;
                    }

                    Log.Warning("[{Caller}] Deneme {Attempt}/{MaxRetries} başarısız, {Delay}ms sonra tekrar deneniyor: {Error}",
                        callerName, attempt, maxRetries, delay, ex.Message);

                    await Task.Delay(delay);
                    delay = Math.Min(delay * 2, maxDelayMs); // Exponential backoff
                }
            }

            // Son deneme
            return await action();
        }

        /// <summary>
        /// Void action için retry
        /// </summary>
        public static async Task ExecuteWithRetryAsync(
            Func<Task> action,
            int maxRetries = 3,
            int initialDelayMs = 100,
            int maxDelayMs = 5000,
            [CallerMemberName] string? callerName = null)
        {
            await ExecuteWithRetryAsync(async () =>
            {
                await action();
                return true;
            }, maxRetries, initialDelayMs, maxDelayMs, callerName: callerName);
        }
    }
}
