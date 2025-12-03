using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.App.Infrastructure
{
    /// <summary>
    /// DÜZELTME v19: Genel Retry Policy ve Resilience Pattern
    /// Exponential backoff, circuit breaker, timeout yönetimi
    /// </summary>
    public static class RetryPolicy
    {
        #region Configuration

        public static class Defaults
        {
            public const int MaxRetries = 3;
            public const int InitialDelayMs = 100;
            public const int MaxDelayMs = 30000;
            public const double BackoffMultiplier = 2.0;
            public const double JitterFactor = 0.1;
        }

        #endregion

        #region Execute Methods

        /// <summary>
        /// Async işlemi retry ile çalıştır
        /// </summary>
        public static async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> action,
            RetryOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= RetryOptions.Default;
            var attempt = 0;
            var delay = options.InitialDelayMs;
            var exceptions = new List<Exception>();

            while (true)
            {
                attempt++;

                try
                {
                    using var timeoutCts = new CancellationTokenSource(options.TimeoutMs);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                    return await action(linkedCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // User cancellation - don't retry
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);

                    if (attempt >= options.MaxRetries || !options.ShouldRetry(ex))
                    {
                        Log.Warning(ex, "[Retry] Başarısız. Attempt: {Attempt}/{MaxRetries}",
                            attempt, options.MaxRetries);

                        throw new RetryExhaustedException(
                            $"Operation failed after {attempt} attempts",
                            exceptions);
                    }

                    // Jitter ekle
                    var jitter = (int)(delay * Defaults.JitterFactor * Random.Shared.NextDouble());
                    var actualDelay = delay + jitter;

                    Log.Debug("[Retry] Attempt {Attempt} failed. Retrying in {Delay}ms. Error: {Error}",
                        attempt, actualDelay, ex.Message);

                    await Task.Delay(actualDelay, ct);

                    // Exponential backoff
                    delay = Math.Min((int)(delay * options.BackoffMultiplier), options.MaxDelayMs);
                }
            }
        }

        /// <summary>
        /// Async işlemi retry ile çalıştır (void)
        /// </summary>
        public static async Task ExecuteAsync(
            Func<CancellationToken, Task> action,
            RetryOptions? options = null,
            CancellationToken ct = default)
        {
            await ExecuteAsync<object?>(async ct2 =>
            {
                await action(ct2);
                return null;
            }, options, ct);
        }

        /// <summary>
        /// Sync işlemi retry ile çalıştır
        /// </summary>
        public static T Execute<T>(
            Func<T> action,
            RetryOptions? options = null)
        {
            options ??= RetryOptions.Default;
            var attempt = 0;
            var delay = options.InitialDelayMs;
            var exceptions = new List<Exception>();

            while (true)
            {
                attempt++;

                try
                {
                    return action();
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);

                    if (attempt >= options.MaxRetries || !options.ShouldRetry(ex))
                    {
                        throw new RetryExhaustedException(
                            $"Operation failed after {attempt} attempts",
                            exceptions);
                    }

                    var jitter = (int)(delay * Defaults.JitterFactor * Random.Shared.NextDouble());
                    Thread.Sleep(delay + jitter);

                    delay = Math.Min((int)(delay * options.BackoffMultiplier), options.MaxDelayMs);
                }
            }
        }

        #endregion

        #region HTTP Specific

        /// <summary>
        /// HTTP isteği retry ile gönder
        /// </summary>
        public static async Task<HttpResponseMessage> ExecuteHttpAsync(
            Func<CancellationToken, Task<HttpResponseMessage>> action,
            RetryOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= RetryOptions.ForHttp();

            return await ExecuteAsync(async ct2 =>
            {
                var response = await action(ct2);

                // Retry yapılabilir HTTP status kodları
                if (options.RetryableStatusCodes.Contains(response.StatusCode))
                {
                    throw new HttpRetryException(response.StatusCode);
                }

                return response;
            }, options, ct);
        }

        #endregion

        #region Circuit Breaker

        /// <summary>
        /// Circuit breaker ile işlem çalıştır
        /// </summary>
        public static async Task<T> ExecuteWithCircuitBreakerAsync<T>(
            string operationKey,
            Func<CancellationToken, Task<T>> action,
            RetryOptions? options = null,
            CancellationToken ct = default)
        {
            var circuitBreaker = CircuitBreakerRegistry.GetOrCreate(operationKey);

            if (circuitBreaker.IsOpen)
            {
                throw new CircuitBreakerOpenException(operationKey, circuitBreaker.OpenedAt);
            }

            try
            {
                var result = await ExecuteAsync(action, options, ct);
                circuitBreaker.RecordSuccess();
                return result;
            }
            catch (Exception)
            {
                circuitBreaker.RecordFailure();
                throw;
            }
        }

        #endregion
    }

    #region Options

    public class RetryOptions
    {
        public int MaxRetries { get; init; } = RetryPolicy.Defaults.MaxRetries;
        public int InitialDelayMs { get; init; } = RetryPolicy.Defaults.InitialDelayMs;
        public int MaxDelayMs { get; init; } = RetryPolicy.Defaults.MaxDelayMs;
        public double BackoffMultiplier { get; init; } = RetryPolicy.Defaults.BackoffMultiplier;
        public int TimeoutMs { get; init; } = 30000;
        public Func<Exception, bool> ShouldRetry { get; init; } = DefaultShouldRetry;
        public HashSet<HttpStatusCode> RetryableStatusCodes { get; init; } = DefaultRetryableStatusCodes;

        public static RetryOptions Default => new();

        public static RetryOptions ForHttp() => new()
        {
            MaxRetries = 3,
            InitialDelayMs = 500,
            TimeoutMs = 30000,
            ShouldRetry = IsRetryableHttpException
        };

        public static RetryOptions ForDatabase() => new()
        {
            MaxRetries = 5,
            InitialDelayMs = 100,
            MaxDelayMs = 5000,
            TimeoutMs = 60000
        };

        public static RetryOptions Quick() => new()
        {
            MaxRetries = 2,
            InitialDelayMs = 50,
            MaxDelayMs = 500,
            TimeoutMs = 5000
        };

        public static RetryOptions Aggressive() => new()
        {
            MaxRetries = 10,
            InitialDelayMs = 100,
            MaxDelayMs = 60000,
            TimeoutMs = 120000
        };

        private static bool DefaultShouldRetry(Exception ex)
        {
            return ex switch
            {
                OperationCanceledException => false,
                OutOfMemoryException => false,
                StackOverflowException => false,
                HttpRequestException => true,
                TimeoutException => true,
                System.IO.IOException => true,
                _ => true
            };
        }

        private static bool IsRetryableHttpException(Exception ex)
        {
            return ex switch
            {
                HttpRetryException => true,
                HttpRequestException => true,
                TaskCanceledException => true,
                TimeoutException => true,
                _ => false
            };
        }

        private static readonly HashSet<HttpStatusCode> DefaultRetryableStatusCodes = new()
        {
            HttpStatusCode.RequestTimeout,           // 408
            HttpStatusCode.TooManyRequests,          // 429
            HttpStatusCode.InternalServerError,      // 500
            HttpStatusCode.BadGateway,               // 502
            HttpStatusCode.ServiceUnavailable,       // 503
            HttpStatusCode.GatewayTimeout            // 504
        };
    }

    #endregion

    #region Circuit Breaker

    public class CircuitBreaker
    {
        private readonly object _lock = new();
        private int _failureCount;
        private DateTime? _openedAt;

        public int FailureThreshold { get; init; } = 5;
        public TimeSpan OpenDuration { get; init; } = TimeSpan.FromMinutes(1);

        public bool IsOpen
        {
            get
            {
                lock (_lock)
                {
                    if (_openedAt == null) return false;

                    if (DateTime.UtcNow - _openedAt > OpenDuration)
                    {
                        // Half-open state - allow one try
                        _openedAt = null;
                        _failureCount = FailureThreshold - 1;
                        return false;
                    }

                    return true;
                }
            }
        }

        public DateTime? OpenedAt => _openedAt;

        public void RecordSuccess()
        {
            lock (_lock)
            {
                _failureCount = 0;
                _openedAt = null;
            }
        }

        public void RecordFailure()
        {
            lock (_lock)
            {
                _failureCount++;

                if (_failureCount >= FailureThreshold)
                {
                    _openedAt = DateTime.UtcNow;
                    Log.Warning("[CircuitBreaker] Açıldı. Failures: {Count}", _failureCount);
                }
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _failureCount = 0;
                _openedAt = null;
            }
        }
    }

    public static class CircuitBreakerRegistry
    {
        private static readonly Dictionary<string, CircuitBreaker> _breakers = new();
        private static readonly object _lock = new();

        public static CircuitBreaker GetOrCreate(string key)
        {
            lock (_lock)
            {
                if (!_breakers.TryGetValue(key, out var breaker))
                {
                    breaker = new CircuitBreaker();
                    _breakers[key] = breaker;
                }
                return breaker;
            }
        }

        public static void Reset(string key)
        {
            lock (_lock)
            {
                if (_breakers.TryGetValue(key, out var breaker))
                {
                    breaker.Reset();
                }
            }
        }

        public static void ResetAll()
        {
            lock (_lock)
            {
                foreach (var breaker in _breakers.Values)
                {
                    breaker.Reset();
                }
            }
        }
    }

    #endregion

    #region Exceptions

    public class RetryExhaustedException : Exception
    {
        public IReadOnlyList<Exception> Attempts { get; }

        public RetryExhaustedException(string message, IEnumerable<Exception> attempts)
            : base(message)
        {
            Attempts = new List<Exception>(attempts);
        }
    }

    public class HttpRetryException : Exception
    {
        public HttpStatusCode StatusCode { get; }

        public HttpRetryException(HttpStatusCode statusCode)
            : base($"HTTP {(int)statusCode}")
        {
            StatusCode = statusCode;
        }
    }

    public class CircuitBreakerOpenException : Exception
    {
        public string OperationKey { get; }
        public DateTime? OpenedAt { get; }

        public CircuitBreakerOpenException(string operationKey, DateTime? openedAt)
            : base($"Circuit breaker is open for '{operationKey}'")
        {
            OperationKey = operationKey;
            OpenedAt = openedAt;
        }
    }

    #endregion

    #region Extensions

    public static class RetryExtensions
    {
        /// <summary>
        /// Task'a retry ekle
        /// </summary>
        public static async Task<T> WithRetryAsync<T>(
            this Task<T> task,
            int maxRetries = 3,
            int delayMs = 100)
        {
            return await RetryPolicy.ExecuteAsync(
                _ => task,
                new RetryOptions
                {
                    MaxRetries = maxRetries,
                    InitialDelayMs = delayMs
                });
        }

        /// <summary>
        /// Func'a retry ekle
        /// </summary>
        public static Func<Task<T>> WithRetry<T>(
            this Func<Task<T>> func,
            RetryOptions? options = null)
        {
            return () => RetryPolicy.ExecuteAsync(_ => func(), options);
        }
    }

    #endregion
}
