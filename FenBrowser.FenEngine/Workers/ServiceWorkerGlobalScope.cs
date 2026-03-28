using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.WebAPIs;
using FenBrowser.FenEngine.Storage;

namespace FenBrowser.FenEngine.Workers
{
    /// <summary>
    /// Global scope for Service Workers.
    /// Extends WorkerGlobalScope with SW-specific APIs (caches, clients, etc.)
    /// </summary>
    public class ServiceWorkerGlobalScope : WorkerGlobalScope
    {
        private readonly IStorageBackend _storageBackend;
        private readonly IExecutionContext _context;

        public ServiceWorkerGlobalScope(WorkerRuntime runtime, string origin, string name, IStorageBackend storageBackend)
            : base(runtime, origin, name)
        {
            _storageBackend = storageBackend ?? throw new ArgumentNullException(nameof(storageBackend));
            _context = runtime?.Context;

            InitializeServiceWorkerProperties(origin);
        }

        private void InitializeServiceWorkerProperties(string origin)
        {
            var cacheStorage = new CacheStorage(() => origin, _storageBackend, _context);
            Set("caches", FenValue.FromObject(cacheStorage));

            Set("skipWaiting", FenValue.FromFunction(new FenFunction("skipWaiting", (args, thisVal) =>
            {
                // SW §4.5.2: Signal the manager to skip the waiting phase for this scope
                ServiceWorkerManager.Instance.SkipWaiting(origin);
                return FenValue.FromObject(CreatePromise(() => Task.FromResult(FenValue.Undefined)));
            })));

            Set("clients", FenValue.FromObject(new ServiceWorkerClients(origin, _context)));

            // Events
            Set("oninstall", FenValue.Null);
            Set("onactivate", FenValue.Null);
            Set("onfetch", FenValue.Null);
        }

        public void DispatchExtendableEvent(string type, FenValue evtObj)
        {
             DispatchEventToHandlers(type, evtObj);
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
                    FenBrowser.Core.FenLogger.Warn($"[ServiceWorkerGlobalScope] Detached async operation failed: {ex.Message}",
                        FenBrowser.Core.Logging.LogCategory.ServiceWorker);
                }
            }, System.Threading.CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }

        private FenObject CreatePromise(Func<Task<FenValue>> valueFactory)
        {
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
                var jsPromise = new JsPromise(FenValue.FromFunction(executor), _context);
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

            // Fallback: hand-rolled promise
            var promise = new FenObject();
            _ = RunDetachedAsync(async () =>
            {
                try
                {
                    var result = await valueFactory().ConfigureAwait(false);
                    promise.Set("__state", FenValue.FromString("fulfilled"));
                    promise.Set("__result", result);
                    var onFulfilled = promise.Get("onFulfilled");
                    if (onFulfilled.IsFunction)
                        onFulfilled.AsFunction().Invoke(new[] { result }, null);
                }
                catch (Exception ex)
                {
                    var reason = FenValue.FromString(ex.Message);
                    promise.Set("__state", FenValue.FromString("rejected"));
                    promise.Set("__reason", reason);
                    var onRejected = promise.Get("onRejected");
                    if (onRejected.IsFunction)
                        onRejected.AsFunction().Invoke(new[] { reason }, null);
                }
            });

            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, _) =>
            {
                if (args.Length > 0 && args[0].IsFunction) promise.Set("onFulfilled", args[0]);
                if (args.Length > 1 && args[1].IsFunction) promise.Set("onRejected", args[1]);
                var state = promise.Get("__state");
                if (!state.IsUndefined && state.ToString() == "fulfilled" && args.Length > 0 && args[0].IsFunction)
                    args[0].AsFunction().Invoke(new[] { promise.Get("__result") }, null);
                else if (!state.IsUndefined && state.ToString() == "rejected" && args.Length > 1 && args[1].IsFunction)
                    args[1].AsFunction().Invoke(new[] { promise.Get("__reason") }, null);
                return FenValue.FromObject(promise);
            })));

            return promise;
        }
    }
}
