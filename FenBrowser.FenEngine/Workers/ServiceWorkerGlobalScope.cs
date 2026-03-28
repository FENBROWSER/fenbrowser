using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.Storage;
using FenBrowser.FenEngine.WebAPIs;

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
                // SW §4.5.2: Signal the manager to skip the waiting phase for this scope.
                ServiceWorkerManager.Instance.SkipWaiting(origin);
                return FenValue.FromObject(CreatePromise(() => Task.FromResult(FenValue.Undefined)));
            })));

            Set("clients", FenValue.FromObject(new ServiceWorkerClients(origin, _context)));

            Set("oninstall", FenValue.Null);
            Set("onactivate", FenValue.Null);
            Set("onfetch", FenValue.Null);
        }

        public void DispatchExtendableEvent(string type, FenValue evtObj)
        {
            DispatchEventToHandlers(type, evtObj);
        }

        private FenObject CreatePromise(Func<Task<FenValue>> valueFactory)
        {
            return WorkerPromise.FromTask(valueFactory, _context, nameof(ServiceWorkerGlobalScope));
        }
    }
}
