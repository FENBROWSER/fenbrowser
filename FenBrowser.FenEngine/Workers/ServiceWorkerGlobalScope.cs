using System;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
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

        public ServiceWorkerGlobalScope(WorkerRuntime runtime, string origin, string name, IStorageBackend storageBackend)
            : base(runtime, origin, name)
        {
            _storageBackend = storageBackend ?? throw new ArgumentNullException(nameof(storageBackend));
            
            InitializeServiceWorkerProperties(origin);
        }

        private void InitializeServiceWorkerProperties(string origin)
        {
            // 'caches' property
            // Note: Spec says it returns a CacheStorage object. 
            // Depending on implementation, it might be the SAME object or new one.
            // Usually singleton per global scope.
            var cacheStorage = new CacheStorage(() => origin, _storageBackend);
            Set("caches", FenValue.FromObject(cacheStorage));

            Set("skipWaiting", FenValue.FromFunction(new FenFunction("skipWaiting", (args, thisVal) =>
            {
                 // Trigger skipWaiting in manager
                 // For now, assume auto-skip in mock
                 return FenValue.FromObject(CreatePromise(() => Task.FromResult<IValue>(FenValue.Undefined)));
            })));

            Set("clients", FenValue.FromObject(new FenObject())); // TODO: Implement Clients interface

            // Events
            Set("oninstall", FenValue.Null);
            Set("onactivate", FenValue.Null);
            Set("onfetch", FenValue.Null);
        }

        // Helper to dispatch ExtendableEvent
        public void DispatchExtendableEvent(string type, IValue evtObj)
        {
             var handler = Get($"on{type}");
             if (handler != null && handler.IsFunction)
             {
                 handler.AsFunction().Invoke(new IValue[] { evtObj }, Runtime.Context);
             }
        }

        // --- Promise Helper ---
        private FenObject CreatePromise(Func<Task<IValue>> valueFactory)
        {
            var promise = new FenObject();
            Task.Run(async () =>
            {
                try { var r = await valueFactory(); ResolvePromise(promise, r); }
                catch (Exception ex) { RejectPromise(promise, ex.Message); }
            });
            SetupPromiseThen(promise);
            return promise;
        }

        private void ResolvePromise(FenObject promise, IValue result)
        {
             if (promise.Has("onFulfilled")) promise.Get("onFulfilled").AsFunction()?.Invoke(new[] { result }, null);
             else { promise.Set("__result", result); promise.Set("__state", FenValue.FromString("fulfilled")); }
        }

        private void RejectPromise(FenObject promise, string error)
        {
             if (promise.Has("onRejected")) promise.Get("onRejected").AsFunction()?.Invoke(new[] { FenValue.FromString(error) }, null);
             else { promise.Set("__reason", FenValue.FromString(error)); promise.Set("__state", FenValue.FromString("rejected")); }
        }

        private void SetupPromiseThen(FenObject promise)
        {
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, _) =>
            {
                if (args.Length > 0) promise.Set("onFulfilled", args[0]);
                if (args.Length > 1) promise.Set("onRejected", args[1]);
                var state = promise.Get("__state")?.ToString();
                if (state == "fulfilled") args[0]?.AsFunction()?.Invoke(new[] { promise.Get("__result") }, null);
                else if (state == "rejected") args[1]?.AsFunction()?.Invoke(new[] { promise.Get("__reason") }, null);
                return FenValue.FromObject(promise);
            })));
        }
    }
}
