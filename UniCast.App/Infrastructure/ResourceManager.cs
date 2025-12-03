using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Timer = System.Threading.Timer;

namespace UniCast.App.Infrastructure
{
    /// <summary>
    /// DÜZELTME v19: Merkezi kaynak yönetimi
    /// IDisposable tracking, cleanup, leak detection
    /// </summary>
    public sealed class ResourceManager : IDisposable
    {
        #region Singleton

        private static readonly Lazy<ResourceManager> _instance =
            new(() => new ResourceManager(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static ResourceManager Instance => _instance.Value;

        #endregion

        #region Fields

        private readonly ConcurrentDictionary<string, TrackedResource> _resources = new();
        private readonly ConcurrentDictionary<string, WeakReference<IDisposable>> _weakResources = new();
        private readonly Timer _cleanupTimer;
        private readonly object _disposeLock = new();

        private bool _disposed;
        private int _resourceIdCounter;

        private const int CleanupIntervalMs = 60000; // 1 dakika

        #endregion

        #region Events

        public event EventHandler<ResourceLeakEventArgs>? OnResourceLeak;

        #endregion

        #region Constructor

        private ResourceManager()
        {
            _cleanupTimer = new Timer(PerformCleanup, null, CleanupIntervalMs, CleanupIntervalMs);
        }

        #endregion

        #region Resource Registration

        /// <summary>
        /// Disposable kaynağı kaydet (strong reference)
        /// </summary>
        public string Register<T>(T resource, [CallerMemberName] string? caller = null) where T : IDisposable
        {
            var id = GenerateResourceId(typeof(T).Name);

            var tracked = new TrackedResource
            {
                Id = id,
                Resource = resource,
                ResourceType = typeof(T).FullName ?? typeof(T).Name,
                RegisteredAt = DateTime.UtcNow,
                RegisteredBy = caller ?? "Unknown",
                StackTrace = Environment.StackTrace
            };

            _resources[id] = tracked;

            Log.Debug("[ResourceManager] Registered: {Id} ({Type})", id, tracked.ResourceType);

            return id;
        }

        /// <summary>
        /// Disposable kaynağı kaydet (weak reference - GC tarafından temizlenebilir)
        /// </summary>
        public string RegisterWeak<T>(T resource, [CallerMemberName] string? caller = null) where T : IDisposable
        {
            var id = GenerateResourceId(typeof(T).Name);
            _weakResources[id] = new WeakReference<IDisposable>(resource);

            Log.Debug("[ResourceManager] Registered (weak): {Id} ({Type})", id, typeof(T).Name);

            return id;
        }

        /// <summary>
        /// Scoped resource - using bloğu sonunda otomatik temizlenir
        /// </summary>
        public ResourceScope<T> CreateScope<T>(T resource, [CallerMemberName] string? caller = null) where T : IDisposable
        {
            return new ResourceScope<T>(this, resource, caller);
        }

        /// <summary>
        /// Birden fazla kaynağı grup olarak kaydet
        /// </summary>
        public ResourceGroup CreateGroup(string groupName)
        {
            return new ResourceGroup(this, groupName);
        }

        #endregion

        #region Resource Disposal

        /// <summary>
        /// Belirli bir kaynağı dispose et
        /// </summary>
        public bool Release(string id)
        {
            if (_resources.TryRemove(id, out var tracked))
            {
                try
                {
                    tracked.Resource.Dispose();
                    Log.Debug("[ResourceManager] Released: {Id}", id);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[ResourceManager] Dispose hatası: {Id}", id);
                }
            }

            if (_weakResources.TryRemove(id, out var weakRef))
            {
                if (weakRef.TryGetTarget(out var resource))
                {
                    try
                    {
                        resource.Dispose();
                        return true;
                    }
                    catch { }
                }
            }

            return false;
        }

        /// <summary>
        /// Belirli türdeki tüm kaynakları dispose et
        /// </summary>
        public int ReleaseAllOfType<T>() where T : IDisposable
        {
            var typeName = typeof(T).FullName;
            var count = 0;

            foreach (var kvp in _resources)
            {
                if (kvp.Value.ResourceType == typeName)
                {
                    if (Release(kvp.Key))
                        count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Tüm kaynakları dispose et
        /// </summary>
        public int ReleaseAll()
        {
            var count = 0;

            foreach (var id in _resources.Keys)
            {
                if (Release(id))
                    count++;
            }

            foreach (var id in _weakResources.Keys)
            {
                if (Release(id))
                    count++;
            }

            return count;
        }

        #endregion

        #region Resource Tracking

        /// <summary>
        /// Kayıtlı kaynak sayısı
        /// </summary>
        public int Count => _resources.Count + _weakResources.Count;

        /// <summary>
        /// Kayıtlı kaynakları listele
        /// </summary>
        public IEnumerable<ResourceInfo> GetRegisteredResources()
        {
            foreach (var tracked in _resources.Values)
            {
                yield return new ResourceInfo
                {
                    Id = tracked.Id,
                    Type = tracked.ResourceType,
                    RegisteredAt = tracked.RegisteredAt,
                    RegisteredBy = tracked.RegisteredBy,
                    Age = DateTime.UtcNow - tracked.RegisteredAt,
                    IsWeak = false
                };
            }

            foreach (var kvp in _weakResources)
            {
                var isAlive = kvp.Value.TryGetTarget(out _);
                yield return new ResourceInfo
                {
                    Id = kvp.Key,
                    Type = "WeakReference",
                    RegisteredAt = DateTime.MinValue,
                    RegisteredBy = "Unknown",
                    Age = TimeSpan.Zero,
                    IsWeak = true,
                    IsAlive = isAlive
                };
            }
        }

        /// <summary>
        /// Belirli bir süreyi aşan kaynakları bul (potansiyel leak)
        /// </summary>
        public IEnumerable<ResourceInfo> GetPotentialLeaks(TimeSpan threshold)
        {
            var now = DateTime.UtcNow;

            foreach (var tracked in _resources.Values)
            {
                var age = now - tracked.RegisteredAt;
                if (age > threshold)
                {
                    yield return new ResourceInfo
                    {
                        Id = tracked.Id,
                        Type = tracked.ResourceType,
                        RegisteredAt = tracked.RegisteredAt,
                        RegisteredBy = tracked.RegisteredBy,
                        Age = age,
                        IsWeak = false,
                        StackTrace = tracked.StackTrace
                    };
                }
            }
        }

        #endregion

        #region Private Methods

        private string GenerateResourceId(string prefix)
        {
            var id = Interlocked.Increment(ref _resourceIdCounter);
            return $"{prefix}_{id:D6}";
        }

        private void PerformCleanup(object? state)
        {
            try
            {
                // Dead weak reference'ları temizle
                var deadKeys = new List<string>();

                foreach (var kvp in _weakResources)
                {
                    if (!kvp.Value.TryGetTarget(out _))
                    {
                        deadKeys.Add(kvp.Key);
                    }
                }

                foreach (var key in deadKeys)
                {
                    _weakResources.TryRemove(key, out _);
                }

                if (deadKeys.Count > 0)
                {
                    Log.Debug("[ResourceManager] Cleaned up {Count} dead weak references", deadKeys.Count);
                }

                // Uzun süreli kaynakları kontrol et (potansiyel leak)
                var leaks = GetPotentialLeaks(TimeSpan.FromMinutes(30)).ToList();
                if (leaks.Count > 0)
                {
                    foreach (var leak in leaks)
                    {
                        OnResourceLeak?.Invoke(this, new ResourceLeakEventArgs
                        {
                            Resource = leak
                        });
                    }

                    Log.Warning("[ResourceManager] {Count} potansiyel resource leak algılandı", leaks.Count);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[ResourceManager] Cleanup hatası");
            }
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            lock (_disposeLock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            _cleanupTimer.Dispose();

            var released = ReleaseAll();
            Log.Information("[ResourceManager] Disposed. Released {Count} resources", released);

            _resources.Clear();
            _weakResources.Clear();

            OnResourceLeak = null;
        }

        #endregion
    }

    #region Types

    public class TrackedResource
    {
        public string Id { get; init; } = "";
        public IDisposable Resource { get; init; } = null!;
        public string ResourceType { get; init; } = "";
        public DateTime RegisteredAt { get; init; }
        public string RegisteredBy { get; init; } = "";
        public string? StackTrace { get; init; }
    }

    public class ResourceInfo
    {
        public string Id { get; init; } = "";
        public string Type { get; init; } = "";
        public DateTime RegisteredAt { get; init; }
        public string RegisteredBy { get; init; } = "";
        public TimeSpan Age { get; init; }
        public bool IsWeak { get; init; }
        public bool IsAlive { get; init; } = true;
        public string? StackTrace { get; init; }
    }

    public class ResourceLeakEventArgs : EventArgs
    {
        public ResourceInfo Resource { get; init; } = null!;
    }

    #endregion

    #region ResourceScope

    /// <summary>
    /// Scoped resource - using bloğu sonunda otomatik temizlenir
    /// </summary>
    public sealed class ResourceScope<T> : IDisposable where T : IDisposable
    {
        private readonly ResourceManager _manager;
        private readonly string _id;
        private bool _disposed;

        public T Resource { get; }

        internal ResourceScope(ResourceManager manager, T resource, string? caller)
        {
            _manager = manager;
            Resource = resource;
            _id = manager.Register(resource, caller);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _manager.Release(_id);
        }
    }

    #endregion

    #region ResourceGroup

    /// <summary>
    /// Birden fazla kaynağı grup olarak yönet
    /// </summary>
    public sealed class ResourceGroup : IDisposable
    {
        private readonly ResourceManager _manager;
        private readonly string _groupName;
        private readonly List<string> _resourceIds = new();
        private bool _disposed;

        internal ResourceGroup(ResourceManager manager, string groupName)
        {
            _manager = manager;
            _groupName = groupName;
        }

        public ResourceGroup Add<T>(T resource) where T : IDisposable
        {
            var id = _manager.Register(resource, _groupName);
            _resourceIds.Add(id);
            return this;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var id in _resourceIds)
            {
                _manager.Release(id);
            }

            _resourceIds.Clear();
        }
    }

    #endregion

    #region Extensions

    public static class ResourceManagerExtensions
    {
        /// <summary>
        /// Disposable'ı ResourceManager'a kaydet ve döndür
        /// </summary>
        public static T Track<T>(this T disposable, [CallerMemberName] string? caller = null) where T : IDisposable
        {
            ResourceManager.Instance.Register(disposable, caller);
            return disposable;
        }

        /// <summary>
        /// Disposable'ı weak reference olarak kaydet
        /// </summary>
        public static T TrackWeak<T>(this T disposable, [CallerMemberName] string? caller = null) where T : IDisposable
        {
            ResourceManager.Instance.RegisterWeak(disposable, caller);
            return disposable;
        }
    }

    #endregion
}
