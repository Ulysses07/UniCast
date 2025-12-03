using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace UniCast.App.Infrastructure
{
    /// <summary>
    /// DÜZELTME v20: Weak Event Manager
    /// Event subscription'larında memory leak'leri önler
    /// </summary>
    public static class WeakEventManager<TEventSource, TEventArgs>
        where TEventSource : class
        where TEventArgs : EventArgs
    {
        #region Fields

        private static readonly ConditionalWeakTable<TEventSource, EventHandlerList> _sourceToHandlers = new();
        private static readonly object _syncLock = new();

        #endregion

        #region Public Methods

        /// <summary>
        /// Event handler'ı weak reference ile ekle
        /// </summary>
        public static void AddHandler(
            TEventSource source,
            string eventName,
            EventHandler<TEventArgs> handler)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            lock (_syncLock)
            {
                var handlerList = _sourceToHandlers.GetOrCreateValue(source);
                handlerList.AddHandler(eventName, handler);
            }
        }

        /// <summary>
        /// Event handler'ı kaldır
        /// </summary>
        public static void RemoveHandler(
            TEventSource source,
            string eventName,
            EventHandler<TEventArgs> handler)
        {
            if (source == null) return;
            if (handler == null) return;

            lock (_syncLock)
            {
                if (_sourceToHandlers.TryGetValue(source, out var handlerList))
                {
                    handlerList.RemoveHandler(eventName, handler);
                }
            }
        }

        /// <summary>
        /// Event'i tetikle
        /// </summary>
        public static void DeliverEvent(TEventSource source, string eventName, TEventArgs args)
        {
            if (source == null) return;

            List<EventHandler<TEventArgs>>? handlers = null;

            lock (_syncLock)
            {
                if (_sourceToHandlers.TryGetValue(source, out var handlerList))
                {
                    handlers = handlerList.GetHandlers(eventName);
                }
            }

            if (handlers != null)
            {
                foreach (var handler in handlers)
                {
                    try
                    {
                        handler(source, args);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[WeakEvent] Handler hatası: {Event}", eventName);
                    }
                }
            }
        }

        #endregion

        #region EventHandlerList

        private class EventHandlerList
        {
            private readonly Dictionary<string, List<WeakReference<EventHandler<TEventArgs>>>> _handlers = new();

            public void AddHandler(string eventName, EventHandler<TEventArgs> handler)
            {
                if (!_handlers.TryGetValue(eventName, out var list))
                {
                    list = new List<WeakReference<EventHandler<TEventArgs>>>();
                    _handlers[eventName] = list;
                }

                list.Add(new WeakReference<EventHandler<TEventArgs>>(handler));
            }

            public void RemoveHandler(string eventName, EventHandler<TEventArgs> handler)
            {
                if (!_handlers.TryGetValue(eventName, out var list)) return;

                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].TryGetTarget(out var target) && target == handler)
                    {
                        list.RemoveAt(i);
                        break;
                    }
                }
            }

            public List<EventHandler<TEventArgs>>? GetHandlers(string eventName)
            {
                if (!_handlers.TryGetValue(eventName, out var list)) return null;

                var result = new List<EventHandler<TEventArgs>>();
                var deadRefs = new List<int>();

                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].TryGetTarget(out var handler))
                    {
                        result.Add(handler);
                    }
                    else
                    {
                        deadRefs.Add(i);
                    }
                }

                // Dead reference'ları temizle
                for (int i = deadRefs.Count - 1; i >= 0; i--)
                {
                    list.RemoveAt(deadRefs[i]);
                }

                return result.Count > 0 ? result : null;
            }
        }

        #endregion
    }

    /// <summary>
    /// Generic olmayan WeakEventManager helper
    /// WPF'in built-in weak event manager'larını kullanır
    /// </summary>
    public static class WeakEventManagerHelper
    {
        /// <summary>
        /// PropertyChanged için weak subscription (EventHandler tipinde)
        /// </summary>
        public static void SubscribeToPropertyChanged<T>(
            T source,
            EventHandler<System.ComponentModel.PropertyChangedEventArgs> handler,
            string propertyName = "")
            where T : System.ComponentModel.INotifyPropertyChanged
        {
            if (source == null || handler == null) return;
            PropertyChangedEventManager.AddHandler(source, handler, propertyName);
        }

        /// <summary>
        /// PropertyChanged subscription kaldır
        /// </summary>
        public static void UnsubscribeFromPropertyChanged<T>(
            T source,
            EventHandler<System.ComponentModel.PropertyChangedEventArgs> handler,
            string propertyName = "")
            where T : System.ComponentModel.INotifyPropertyChanged
        {
            if (source == null || handler == null) return;
            PropertyChangedEventManager.RemoveHandler(source, handler, propertyName);
        }

        /// <summary>
        /// CollectionChanged için weak subscription (EventHandler tipinde)
        /// </summary>
        public static void SubscribeToCollectionChanged<T>(
            T source,
            EventHandler<System.Collections.Specialized.NotifyCollectionChangedEventArgs> handler)
            where T : System.Collections.Specialized.INotifyCollectionChanged
        {
            if (source == null || handler == null) return;
            CollectionChangedEventManager.AddHandler(source, handler);
        }

        /// <summary>
        /// CollectionChanged subscription kaldır
        /// </summary>
        public static void UnsubscribeFromCollectionChanged<T>(
            T source,
            EventHandler<System.Collections.Specialized.NotifyCollectionChangedEventArgs> handler)
            where T : System.Collections.Specialized.INotifyCollectionChanged
        {
            if (source == null || handler == null) return;
            CollectionChangedEventManager.RemoveHandler(source, handler);
        }

        /// <summary>
        /// PropertyChanged için direkt subscription (delegate tipinde)
        /// Not: Bu metod weak reference kullanmaz, sadece kolaylık sağlar
        /// </summary>
        public static IDisposable SubscribePropertyChangedDirect<T>(
            T source,
            System.ComponentModel.PropertyChangedEventHandler handler)
            where T : System.ComponentModel.INotifyPropertyChanged
        {
            if (source == null || handler == null)
                return new EmptyDisposable();

            source.PropertyChanged += handler;
            return new PropertyChangedSubscription<T>(source, handler);
        }

        /// <summary>
        /// CollectionChanged için direkt subscription (delegate tipinde)
        /// Not: Bu metod weak reference kullanmaz, sadece kolaylık sağlar
        /// </summary>
        public static IDisposable SubscribeCollectionChangedDirect<T>(
            T source,
            System.Collections.Specialized.NotifyCollectionChangedEventHandler handler)
            where T : System.Collections.Specialized.INotifyCollectionChanged
        {
            if (source == null || handler == null)
                return new EmptyDisposable();

            source.CollectionChanged += handler;
            return new CollectionChangedSubscription<T>(source, handler);
        }

        #region Subscription Classes

        private sealed class EmptyDisposable : IDisposable
        {
            public void Dispose() { }
        }

        private sealed class PropertyChangedSubscription<T> : IDisposable
            where T : System.ComponentModel.INotifyPropertyChanged
        {
            private T? _source;
            private System.ComponentModel.PropertyChangedEventHandler? _handler;

            public PropertyChangedSubscription(T source, System.ComponentModel.PropertyChangedEventHandler handler)
            {
                _source = source;
                _handler = handler;
            }

            public void Dispose()
            {
                if (_source != null && _handler != null)
                {
                    _source.PropertyChanged -= _handler;
                    _source = default;
                    _handler = null;
                }
            }
        }

        private sealed class CollectionChangedSubscription<T> : IDisposable
            where T : System.Collections.Specialized.INotifyCollectionChanged
        {
            private T? _source;
            private System.Collections.Specialized.NotifyCollectionChangedEventHandler? _handler;

            public CollectionChangedSubscription(T source, System.Collections.Specialized.NotifyCollectionChangedEventHandler handler)
            {
                _source = source;
                _handler = handler;
            }

            public void Dispose()
            {
                if (_source != null && _handler != null)
                {
                    _source.CollectionChanged -= _handler;
                    _source = default;
                    _handler = null;
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// Event subscription scope - using bloğu sonunda otomatik unsubscribe
    /// </summary>
    public sealed class EventSubscription : IDisposable
    {
        private readonly Action _unsubscribe;
        private bool _disposed;

        public EventSubscription(Action subscribe, Action unsubscribe)
        {
            _unsubscribe = unsubscribe ?? throw new ArgumentNullException(nameof(unsubscribe));
            subscribe?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _unsubscribe();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[EventSubscription] Unsubscribe hatası");
            }
        }
    }

    /// <summary>
    /// Event subscription builder
    /// </summary>
    public static class EventSubscriptionExtensions
    {
        /// <summary>
        /// Scoped event subscription oluştur
        /// </summary>
        public static EventSubscription SubscribeScoped<TSource, TArgs>(
            this TSource source,
            Action<TSource, EventHandler<TArgs>> subscribe,
            Action<TSource, EventHandler<TArgs>> unsubscribe,
            EventHandler<TArgs> handler)
            where TArgs : EventArgs
        {
            return new EventSubscription(
                () => subscribe(source, handler),
                () => unsubscribe(source, handler));
        }
    }
}