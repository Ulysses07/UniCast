using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.App.Infrastructure
{
    /// <summary>
    /// DÜZELTME v20: Async Void Handler
    /// WPF event handler'larında async void kullanımını güvenli hale getirir
    /// 
    /// PROBLEM:
    /// - async void metodlarda exception'lar yakalanmaz
    /// - Uygulama beklenmedik şekilde crash olabilir
    /// - Debugging zorlaşır
    /// 
    /// ÇÖZÜM:
    /// Bu sınıfı kullanarak tüm async event handler'ları sarmalayın
    /// </summary>
    public static class AsyncVoidHandler
    {
        #region Event Handler Wrappers

        /// <summary>
        /// Async event handler'ı güvenli şekilde çalıştır
        /// </summary>
        /// <example>
        /// // ÖNCE (güvensiz):
        /// private async void Button_Click(object sender, System.Windows.RoutedEventArgs e)
        /// {
        ///     await DoSomethingAsync();
        /// }
        /// 
        /// // SONRA (güvenli):
        /// private void Button_Click(object sender, System.Windows.RoutedEventArgs e)
        /// {
        ///     AsyncVoidHandler.Handle(async () => await DoSomethingAsync());
        /// }
        /// </example>
        public static async void Handle(
            Func<Task> asyncAction,
            Action<Exception>? errorHandler = null,
            bool showErrorDialog = true,
            [CallerMemberName] string? callerName = null)
        {
            try
            {
                await asyncAction();
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation - ignore
                Log.Debug("[AsyncHandler] {Caller} iptal edildi", callerName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AsyncHandler] {Caller} hatası", callerName);

                if (errorHandler != null)
                {
                    try
                    {
                        errorHandler(ex);
                    }
                    catch (Exception handlerEx)
                    {
                        Log.Error(handlerEx, "[AsyncHandler] Error handler hatası");
                    }
                }

                if (showErrorDialog)
                {
                    ShowErrorOnUIThread(ex, callerName);
                }
            }
        }

        /// <summary>
        /// Async event handler'ı parametrelerle güvenli şekilde çalıştır
        /// </summary>
        public static async void Handle<T>(
            T parameter,
            Func<T, Task> asyncAction,
            Action<Exception>? errorHandler = null,
            bool showErrorDialog = true,
            [CallerMemberName] string? callerName = null)
        {
            try
            {
                await asyncAction(parameter);
            }
            catch (OperationCanceledException)
            {
                Log.Debug("[AsyncHandler] {Caller} iptal edildi", callerName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AsyncHandler] {Caller} hatası", callerName);
                errorHandler?.Invoke(ex);

                if (showErrorDialog)
                {
                    ShowErrorOnUIThread(ex, callerName);
                }
            }
        }

        /// <summary>
        /// Command için async handler
        /// </summary>
        public static async void HandleCommand(
            Func<Task> asyncAction,
            Action? onCompleted = null,
            Action<Exception>? onError = null,
            [CallerMemberName] string? callerName = null)
        {
            try
            {
                await asyncAction();
                onCompleted?.Invoke();
            }
            catch (OperationCanceledException)
            {
                Log.Debug("[AsyncHandler] Command {Caller} iptal edildi", callerName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AsyncHandler] Command {Caller} hatası", callerName);
                onError?.Invoke(ex);
            }
        }

        #endregion

        #region Fire and Forget

        /// <summary>
        /// Task'ı fire-and-forget olarak çalıştır (güvenli)
        /// </summary>
        public static async void FireAndForget(
            this Task task,
            Action<Exception>? errorHandler = null,
            [CallerMemberName] string? callerName = null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[AsyncHandler] FireAndForget {Caller} hatası", callerName);
                errorHandler?.Invoke(ex);
            }
        }

        /// <summary>
        /// Action'ı fire-and-forget olarak çalıştır
        /// </summary>
        public static void FireAndForget(
            Func<Task> asyncAction,
            Action<Exception>? errorHandler = null,
            [CallerMemberName] string? callerName = null)
        {
            asyncAction().FireAndForget(errorHandler, callerName);
        }

        #endregion

        #region UI Thread Helpers

        /// <summary>
        /// UI thread'de async işlem çalıştır
        /// </summary>
        public static async void OnUIThread(
            Func<Task> asyncAction,
            [CallerMemberName] string? callerName = null)
        {
            try
            {
                if (System.Windows.Application.Current?.Dispatcher == null)
                {
                    await asyncAction();
                    return;
                }

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await asyncAction();
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AsyncHandler] UI thread {Caller} hatası", callerName);
                ShowErrorOnUIThread(ex, callerName);
            }
        }

        /// <summary>
        /// UI thread'de sync işlem çalıştır
        /// </summary>
        public static void InvokeOnUIThread(Action action)
        {
            if (System.Windows.Application.Current?.Dispatcher == null)
            {
                action();
                return;
            }

            if (System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                System.Windows.Application.Current.Dispatcher.Invoke(action);
            }
        }

        #endregion

        #region Error Display

        private static void ShowErrorOnUIThread(Exception ex, string? context)
        {
            try
            {
                var message = ex is Exceptions.LocalizedException localizedEx
                    ? localizedEx.UserMessage
                    : "Beklenmeyen bir hata oluştu.";

                InvokeOnUIThread(() =>
                {
                    System.Windows.MessageBox.Show(
                        message,
                        "Hata",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                });
            }
            catch (Exception displayEx)
            {
                Log.Error(displayEx, "[AsyncHandler] Error dialog gösterilemedi");
            }
        }

        #endregion

        #region Typed Event Handlers

        /// <summary>
        /// System.Windows.RoutedEventHandler için async wrapper
        /// </summary>
        public static System.Windows.RoutedEventHandler CreateHandler(
            Func<object, System.Windows.RoutedEventArgs, Task> asyncHandler,
            bool showErrorDialog = true)
        {
            return (sender, e) => Handle(
                async () => await asyncHandler(sender, e),
                showErrorDialog: showErrorDialog);
        }

        /// <summary>
        /// Generic EventHandler için async wrapper
        /// </summary>
        public static EventHandler<TEventArgs> CreateHandler<TEventArgs>(
            Func<object?, TEventArgs, Task> asyncHandler,
            bool showErrorDialog = false) where TEventArgs : EventArgs
        {
            return (sender, e) => Handle(
                async () => await asyncHandler(sender, e),
                showErrorDialog: showErrorDialog);
        }

        #endregion
    }

    #region Extension Methods

    public static class AsyncVoidExtensions
    {
        /// <summary>
        /// Task'ı güvenli fire-and-forget olarak çalıştır
        /// </summary>
        public static void SafeFireAndForget(
            this Task task,
            bool continueOnCapturedContext = false,
            Action<Exception>? onException = null)
        {
            task.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    var ex = t.Exception.GetBaseException();
                    Log.Warning(ex, "[SafeFireAndForget] Task hatası");
                    onException?.Invoke(ex);
                }
            }, continueOnCapturedContext
                ? TaskScheduler.FromCurrentSynchronizationContext()
                : TaskScheduler.Default);
        }

        /// <summary>
        /// ValueTask'ı güvenli fire-and-forget olarak çalıştır
        /// </summary>
        public static void SafeFireAndForget(
            this ValueTask valueTask,
            Action<Exception>? onException = null)
        {
            if (valueTask.IsCompletedSuccessfully)
                return;

            valueTask.AsTask().SafeFireAndForget(onException: onException);
        }
    }

    #endregion

    #region Async Command Base

    /// <summary>
    /// Async ICommand implementasyonu
    /// </summary>
    public class AsyncRelayCommand : System.Windows.Input.ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool>? _canExecute;
        private bool _isExecuting;

        public event EventHandler? CanExecuteChanged
        {
            add => System.Windows.Input.CommandManager.RequerySuggested += value;
            remove => System.Windows.Input.CommandManager.RequerySuggested -= value;
        }

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
        {
            return !_isExecuting && (_canExecute?.Invoke() ?? true);
        }

        public async void Execute(object? parameter)
        {
            if (_isExecuting) return;

            _isExecuting = true;
            RaiseCanExecuteChanged();

            try
            {
                await _execute();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AsyncCommand] Execute hatası");
            }
            finally
            {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged()
        {
            AsyncVoidHandler.InvokeOnUIThread(() =>
                System.Windows.Input.CommandManager.InvalidateRequerySuggested());
        }
    }

    /// <summary>
    /// Parametreli async command
    /// </summary>
    public class AsyncRelayCommand<T> : System.Windows.Input.ICommand
    {
        private readonly Func<T?, Task> _execute;
        private readonly Func<T?, bool>? _canExecute;
        private bool _isExecuting;

        public event EventHandler? CanExecuteChanged
        {
            add => System.Windows.Input.CommandManager.RequerySuggested += value;
            remove => System.Windows.Input.CommandManager.RequerySuggested -= value;
        }

        public AsyncRelayCommand(Func<T?, Task> execute, Func<T?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
        {
            return !_isExecuting && (_canExecute?.Invoke((T?)parameter) ?? true);
        }

        public async void Execute(object? parameter)
        {
            if (_isExecuting) return;

            _isExecuting = true;

            try
            {
                await _execute((T?)parameter);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AsyncCommand<T>] Execute hatası");
            }
            finally
            {
                _isExecuting = false;
            }
        }
    }

    #endregion
}