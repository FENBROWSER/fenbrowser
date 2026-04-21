using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Storage;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Represents the CacheStorage interface (window.caches / self.caches).
    /// Manages named Cache objects.
    /// </summary>
    public class CacheStorage : FenObject
    {
        private readonly IStorageBackend _storageBackend;
        private readonly Func<string> _originProvider;
        private readonly Dictionary<string, Cache> _openCaches = new(StringComparer.Ordinal);
        private readonly HashSet<string> _knownCacheNames = new(StringComparer.Ordinal);
        private readonly object _cacheGate = new object();
        private readonly IExecutionContext _context;

        private const int MaxCacheNameLength = 128;

        public CacheStorage(Func<string> originProvider, IStorageBackend storageBackend, IExecutionContext context = null)
        {
            _originProvider = originProvider;
            _storageBackend = storageBackend;
            _context = context;

            Set("open", FenValue.FromFunction(new FenFunction("open", Open)));
            Set("has", FenValue.FromFunction(new FenFunction("has", Has)));
            Set("delete", FenValue.FromFunction(new FenFunction("delete", Delete)));
            Set("keys", FenValue.FromFunction(new FenFunction("keys", Keys)));
            Set("match", FenValue.FromFunction(new FenFunction("match", Match)));
        }

        private FenValue Open(FenValue[] args, FenValue thisVal)
        {
            if (args == null || args.Length < 1)
            {
                return FenValue.FromObject(CreateRejectedPromise("CacheStorage.open requires a cache name."));
            }

            var cacheName = NormalizeCacheName(args[0].ToString());
            if (cacheName == null)
            {
                return FenValue.FromObject(CreateRejectedPromise("CacheStorage.open received an invalid cache name."));
            }

            return FenValue.FromObject(CreatePromise(() =>
            {
                var cache = GetOrCreateCache(cacheName);
                return Task.FromResult(FenValue.FromObject(cache));
            }));
        }

        private FenValue Has(FenValue[] args, FenValue thisVal)
        {
            if (args == null || args.Length < 1)
            {
                return FenValue.FromObject(CreatePromise(() => Task.FromResult(FenValue.FromBoolean(false))));
            }

            var cacheName = NormalizeCacheName(args[0].ToString());
            if (cacheName == null)
            {
                return FenValue.FromObject(CreatePromise(() => Task.FromResult(FenValue.FromBoolean(false))));
            }

            return FenValue.FromObject(CreatePromise(async () =>
            {
                lock (_cacheGate)
                {
                    if (_knownCacheNames.Contains(cacheName))
                    {
                        return FenValue.FromBoolean(true);
                    }
                }

                var info = await _storageBackend.GetDatabaseInfo(_originProvider(), GetDatabaseName(cacheName)).ConfigureAwait(false);
                return FenValue.FromBoolean(info != null);
            }));
        }

        private FenValue Delete(FenValue[] args, FenValue thisVal)
        {
            if (args == null || args.Length < 1)
            {
                return FenValue.FromObject(CreatePromise(() => Task.FromResult(FenValue.FromBoolean(false))));
            }

            var cacheName = NormalizeCacheName(args[0].ToString());
            if (cacheName == null)
            {
                return FenValue.FromObject(CreatePromise(() => Task.FromResult(FenValue.FromBoolean(false))));
            }

            return FenValue.FromObject(CreatePromise(async () =>
            {
                var existed = false;
                Cache removedCache = null;

                lock (_cacheGate)
                {
                    if (_openCaches.TryGetValue(cacheName, out removedCache))
                    {
                        _openCaches.Remove(cacheName);
                        existed = true;
                    }

                    if (_knownCacheNames.Remove(cacheName))
                    {
                        existed = true;
                    }
                }

                removedCache?.Invalidate();

                var dbInfo = await _storageBackend.GetDatabaseInfo(_originProvider(), GetDatabaseName(cacheName)).ConfigureAwait(false);
                if (dbInfo != null)
                {
                    await _storageBackend.DeleteDatabase(_originProvider(), GetDatabaseName(cacheName)).ConfigureAwait(false);
                    existed = true;
                }

                return FenValue.FromBoolean(existed);
            }));
        }

        private FenValue Keys(FenValue[] args, FenValue thisVal)
        {
            return FenValue.FromObject(CreatePromise(() =>
            {
                var names = SnapshotKnownCacheNames();
                names.Sort(StringComparer.Ordinal);

                var array = new FenObject();
                array.Set("length", FenValue.FromNumber(names.Count));
                for (var i = 0; i < names.Count; i++)
                {
                    array.Set(i.ToString(), FenValue.FromString(names[i]));
                }

                return Task.FromResult(FenValue.FromObject(array));
            }));
        }

        private FenValue Match(FenValue[] args, FenValue thisVal)
        {
            if (args == null || args.Length < 1)
            {
                return FenValue.FromObject(CreateRejectedPromise("CacheStorage.match requires a request argument."));
            }

            return FenValue.FromObject(CreatePromise(async () =>
            {
                var requestUrl = Cache.ResolveRequestUrl(args[0]);
                Cache.EnsureAllowedRequestUrl(requestUrl);

                var names = SnapshotKnownCacheNames();
                foreach (var cacheName in names)
                {
                    var cache = GetOrCreateCache(cacheName);
                    var matched = await cache.MatchRequestAsync(requestUrl).ConfigureAwait(false);
                    if (!matched.IsUndefined)
                    {
                        return matched;
                    }
                }

                return FenValue.Undefined;
            }));
        }

        private Cache GetOrCreateCache(string cacheName)
        {
            lock (_cacheGate)
            {
                if (!_openCaches.TryGetValue(cacheName, out var cache))
                {
                    cache = new Cache(_originProvider(), cacheName, _storageBackend, _context);
                    _openCaches[cacheName] = cache;
                }

                _knownCacheNames.Add(cacheName);
                return cache;
            }
        }

        private List<string> SnapshotKnownCacheNames()
        {
            lock (_cacheGate)
            {
                var names = new List<string>(_knownCacheNames);
                foreach (var key in _openCaches.Keys)
                {
                    if (!names.Contains(key))
                    {
                        names.Add(key);
                    }
                }

                return names;
            }
        }

        private static string NormalizeCacheName(string cacheName)
        {
            if (string.IsNullOrWhiteSpace(cacheName))
            {
                return null;
            }

            var trimmed = cacheName.Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxCacheNameLength)
            {
                return null;
            }

            for (var i = 0; i < trimmed.Length; i++)
            {
                if (char.IsControl(trimmed[i]))
                {
                    return null;
                }
            }

            return trimmed;
        }

        private static string GetDatabaseName(string cacheName)
        {
            return $"cache_{cacheName}";
        }

        private static Task RunDetachedAsync(Func<Task> operation)
        {
            return Task.Factory.StartNew(async () =>
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FenBrowser.Core.EngineLogCompat.Warn($"[CacheStorage] Detached async operation failed: {ex.Message}", LogCategory.Storage);
                }
            }, System.Threading.CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }

        private FenObject CreateRejectedPromise(string message)
        {
            return CreatePromise(() => throw new InvalidOperationException(message));
        }

        private FenObject CreatePromise(Func<Task<FenValue>> valueFactory)
        {
            // When a real IExecutionContext is available, use spec-compliant JsPromise
            if (_context != null)
            {
                FenValue capturedResolve = FenValue.Undefined;
                FenValue capturedReject = FenValue.Undefined;
                var executor = new FenFunction("executor", (args, thisVal) =>
                {
                    capturedResolve = args.Length > 0 ? args[0] : FenValue.Undefined;
                    capturedReject = args.Length > 1 ? args[1] : FenValue.Undefined;
                    return FenValue.Undefined;
                });
                var jsPromise = new Core.Types.JsPromise(FenValue.FromFunction(executor), _context);
                _ = RunDetachedAsync(async () =>
                {
                    try
                    {
                        var value = await valueFactory().ConfigureAwait(false);
                        if (capturedResolve.IsFunction)
                            capturedResolve.AsFunction().Invoke(new[] { value }, _context);
                    }
                    catch (Exception ex)
                    {
                        if (capturedReject.IsFunction)
                            capturedReject.AsFunction().Invoke(new[] { FenValue.FromString(ex.Message) }, _context);
                    }
                });
                return jsPromise;
            }

            // Fallback: hand-rolled promise for standalone/test contexts without IExecutionContext
            var promise = new FenObject();
            var gate = new object();
            var state = "pending";
            var settledValue = FenValue.Undefined;
            var fulfilledHandlers = new List<FenFunction>();
            var rejectedHandlers = new List<FenFunction>();

            promise.Set("__state", FenValue.FromString(state));

            void Settle(string nextState, FenValue value)
            {
                List<FenFunction> handlers;
                lock (gate)
                {
                    if (!string.Equals(state, "pending", StringComparison.Ordinal))
                    {
                        return;
                    }

                    state = nextState;
                    settledValue = value;

                    promise.Set("__state", FenValue.FromString(state));
                    if (string.Equals(state, "fulfilled", StringComparison.Ordinal))
                    {
                        promise.Set("__result", value);
                        handlers = new List<FenFunction>(fulfilledHandlers);
                    }
                    else
                    {
                        promise.Set("__reason", value);
                        handlers = new List<FenFunction>(rejectedHandlers);
                    }
                }

                foreach (var handler in handlers)
                {
                    TryInvokePromiseCallback(handler, value);
                }
            }

            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, thisValue) =>
            {
                FenFunction onFulfilled = null;
                FenFunction onRejected = null;
                if (args != null && args.Length > 0 && args[0].IsFunction)
                {
                    onFulfilled = args[0].AsFunction();
                }
                if (args != null && args.Length > 1 && args[1].IsFunction)
                {
                    onRejected = args[1].AsFunction();
                }

                string currentState;
                FenValue currentValue;
                lock (gate)
                {
                    if (onFulfilled != null)
                    {
                        fulfilledHandlers.Add(onFulfilled);
                    }
                    if (onRejected != null)
                    {
                        rejectedHandlers.Add(onRejected);
                    }

                    currentState = state;
                    currentValue = settledValue;
                }

                if (string.Equals(currentState, "fulfilled", StringComparison.Ordinal) && onFulfilled != null)
                {
                    TryInvokePromiseCallback(onFulfilled, currentValue);
                }
                else if (string.Equals(currentState, "rejected", StringComparison.Ordinal) && onRejected != null)
                {
                    TryInvokePromiseCallback(onRejected, currentValue);
                }

                return FenValue.FromObject(promise);
            })));

            promise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, thisValue) =>
            {
                FenFunction onRejected = null;
                if (args != null && args.Length > 0 && args[0].IsFunction)
                {
                    onRejected = args[0].AsFunction();
                }

                string currentState;
                FenValue currentValue;
                lock (gate)
                {
                    if (onRejected != null)
                    {
                        rejectedHandlers.Add(onRejected);
                    }

                    currentState = state;
                    currentValue = settledValue;
                }

                if (string.Equals(currentState, "rejected", StringComparison.Ordinal) && onRejected != null)
                {
                    TryInvokePromiseCallback(onRejected, currentValue);
                }

                return FenValue.FromObject(promise);
            })));

            _ = RunDetachedAsync(async () =>
            {
                try
                {
                    var value = await valueFactory().ConfigureAwait(false);
                    Settle("fulfilled", value);
                }
                catch (Exception ex)
                {
                    Settle("rejected", FenValue.FromString(ex.Message));
                }
            });

            return promise;
        }

        private static void TryInvokePromiseCallback(FenFunction callback, FenValue value)
        {
            if (callback == null)
            {
                return;
            }

            try
            {
                callback.Invoke(new[] { value }, null);
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Warn($"[CacheStorage] Promise callback failed: {ex.Message}", LogCategory.Storage);
            }
        }
    }
}

