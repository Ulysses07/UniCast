using System;
using System.Collections.Concurrent;
using System.Threading;

namespace UniCast.App.Infrastructure
{
    /// <summary>
    /// DÜZELTME v20: Thread Safety Helpers
    /// Thread-safe singleton, lazy initialization, atomic operations
    /// </summary>
    public static class ThreadSafetyHelpers
    {
        #region Atomic Operations

        /// <summary>
        /// Atomic increment with max limit
        /// </summary>
        public static int IncrementWithMax(ref int location, int max)
        {
            int initial, computed;
            do
            {
                initial = location;
                computed = initial >= max ? initial : initial + 1;
            }
            while (Interlocked.CompareExchange(ref location, computed, initial) != initial);

            return computed;
        }

        /// <summary>
        /// Atomic decrement with min limit
        /// </summary>
        public static int DecrementWithMin(ref int location, int min)
        {
            int initial, computed;
            do
            {
                initial = location;
                computed = initial <= min ? initial : initial - 1;
            }
            while (Interlocked.CompareExchange(ref location, computed, initial) != initial);

            return computed;
        }

        /// <summary>
        /// Atomic compare and swap for reference types
        /// </summary>
        public static bool CompareAndSwap<T>(ref T location, T expected, T newValue) where T : class
        {
            return Interlocked.CompareExchange(ref location, newValue, expected) == expected;
        }

        /// <summary>
        /// Thread-safe bool flag
        /// </summary>
        public static bool TrySetFlag(ref int flag)
        {
            return Interlocked.CompareExchange(ref flag, 1, 0) == 0;
        }

        /// <summary>
        /// Thread-safe bool flag reset
        /// </summary>
        public static void ResetFlag(ref int flag)
        {
            Interlocked.Exchange(ref flag, 0);
        }

        #endregion

        #region Lazy Initialization

        /// <summary>
        /// Double-checked locking pattern
        /// </summary>
        public static T EnsureInitialized<T>(
            ref T? target,
            ref object? syncLock,
            Func<T> factory) where T : class
        {
            if (target != null) return target;

            var lockObj = syncLock ?? Interlocked.CompareExchange(ref syncLock, new object(), null) ?? syncLock;

            lock (lockObj!)
            {
                if (target == null)
                {
                    target = factory();
                }
            }

            return target;
        }

        /// <summary>
        /// Lock-free lazy initialization (for value types)
        /// </summary>
        public static T LazyInitialize<T>(
            ref T target,
            ref int initialized,
            Func<T> factory) where T : struct
        {
            if (Volatile.Read(ref initialized) == 1)
            {
                return target;
            }

            var value = factory();

            if (Interlocked.CompareExchange(ref initialized, 1, 0) == 0)
            {
                target = value;
            }

            return target;
        }

        #endregion
    }

    #region Thread-Safe Singleton Base

    /// <summary>
    /// Thread-safe singleton base class
    /// </summary>
    public abstract class ThreadSafeSingleton<T> where T : ThreadSafeSingleton<T>, new()
    {
        private static readonly Lazy<T> _instance =
            new(() => new T(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static T Instance => _instance.Value;

        protected ThreadSafeSingleton() { }
    }

    #endregion

    #region Thread-Safe Property

    /// <summary>
    /// Thread-safe property wrapper
    /// </summary>
    public sealed class ThreadSafeProperty<T>
    {
        private T _value;
        private readonly object _lock = new();

        public ThreadSafeProperty(T initialValue = default!)
        {
            _value = initialValue;
        }

        public T Value
        {
            get
            {
                lock (_lock)
                {
                    return _value;
                }
            }
            set
            {
                lock (_lock)
                {
                    _value = value;
                }
            }
        }

        public T GetAndSet(T newValue)
        {
            lock (_lock)
            {
                var old = _value;
                _value = newValue;
                return old;
            }
        }

        public bool CompareAndSet(T expected, T newValue)
        {
            lock (_lock)
            {
                if (EqualityComparer<T>.Default.Equals(_value, expected))
                {
                    _value = newValue;
                    return true;
                }
                return false;
            }
        }

        public T UpdateAndGet(Func<T, T> updateFunc)
        {
            lock (_lock)
            {
                _value = updateFunc(_value);
                return _value;
            }
        }
    }

    /// <summary>
    /// Lock-free thread-safe counter
    /// </summary>
    public sealed class AtomicCounter
    {
        private long _value;

        public AtomicCounter(long initialValue = 0)
        {
            _value = initialValue;
        }

        public long Value => Interlocked.Read(ref _value);

        public long Increment() => Interlocked.Increment(ref _value);
        public long Decrement() => Interlocked.Decrement(ref _value);
        public long Add(long delta) => Interlocked.Add(ref _value, delta);
        public void Reset() => Interlocked.Exchange(ref _value, 0);
        public long GetAndReset() => Interlocked.Exchange(ref _value, 0);
    }

    /// <summary>
    /// Thread-safe boolean flag
    /// </summary>
    public sealed class AtomicBool
    {
        private int _value;

        public AtomicBool(bool initialValue = false)
        {
            _value = initialValue ? 1 : 0;
        }

        public bool Value => Interlocked.CompareExchange(ref _value, 0, 0) == 1;

        public bool Set()
        {
            return Interlocked.CompareExchange(ref _value, 1, 0) == 0;
        }

        public bool Reset()
        {
            return Interlocked.CompareExchange(ref _value, 0, 1) == 1;
        }

        public bool Toggle()
        {
            int initial, computed;
            do
            {
                initial = _value;
                computed = initial == 0 ? 1 : 0;
            }
            while (Interlocked.CompareExchange(ref _value, computed, initial) != initial);

            return computed == 1;
        }
    }

    #endregion

    #region Read-Write Lock Helper

    /// <summary>
    /// ReaderWriterLockSlim wrapper for using pattern
    /// </summary>
    public sealed class ReadWriteLock : IDisposable
    {
        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);
        private bool _disposed;

        public IDisposable ReadLock()
        {
            _lock.EnterReadLock();
            return new LockReleaser(_lock, LockType.Read);
        }

        public IDisposable WriteLock()
        {
            _lock.EnterWriteLock();
            return new LockReleaser(_lock, LockType.Write);
        }

        public IDisposable UpgradeableReadLock()
        {
            _lock.EnterUpgradeableReadLock();
            return new LockReleaser(_lock, LockType.UpgradeableRead);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lock.Dispose();
        }

        private enum LockType { Read, Write, UpgradeableRead }

        private readonly struct LockReleaser : IDisposable
        {
            private readonly ReaderWriterLockSlim _lock;
            private readonly LockType _type;

            public LockReleaser(ReaderWriterLockSlim lockObj, LockType type)
            {
                _lock = lockObj;
                _type = type;
            }

            public void Dispose()
            {
                switch (_type)
                {
                    case LockType.Read:
                        _lock.ExitReadLock();
                        break;
                    case LockType.Write:
                        _lock.ExitWriteLock();
                        break;
                    case LockType.UpgradeableRead:
                        _lock.ExitUpgradeableReadLock();
                        break;
                }
            }
        }
    }

    #endregion

    #region Thread-Safe Collection Extensions

    public static class ThreadSafeCollectionExtensions
    {
        /// <summary>
        /// ConcurrentDictionary için GetOrAdd with lazy factory
        /// </summary>
        public static TValue GetOrAddLazy<TKey, TValue>(
            this ConcurrentDictionary<TKey, Lazy<TValue>> dict,
            TKey key,
            Func<TKey, TValue> valueFactory) where TKey : notnull
        {
            var lazy = dict.GetOrAdd(key, k => new Lazy<TValue>(() => valueFactory(k)));
            return lazy.Value;
        }

        /// <summary>
        /// Thread-safe AddOrUpdate with old value
        /// </summary>
        public static TValue AddOrUpdateWithOld<TKey, TValue>(
            this ConcurrentDictionary<TKey, TValue> dict,
            TKey key,
            TValue addValue,
            Func<TKey, TValue, TValue, TValue> updateFunc) where TKey : notnull
        {
            return dict.AddOrUpdate(key, addValue, (k, old) => updateFunc(k, old, addValue));
        }
    }

    #endregion

    #region Disposable Helpers

    /// <summary>
    /// Thread-safe dispose helper
    /// </summary>
    public abstract class ThreadSafeDisposable : IDisposable
    {
        private int _disposed;

        protected bool IsDisposed => Interlocked.CompareExchange(ref _disposed, 0, 0) == 1;

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }
        }

        protected abstract void Dispose(bool disposing);

        protected void ThrowIfDisposed()
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }
    }

    #endregion
}
