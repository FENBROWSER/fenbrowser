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
            var cacheStorage = new CacheStorage(origin, _storageBackend);
            Set("caches", FenValue.FromObject(cacheStorage));

            // 'skipWaiting' method
            Set("skipWaiting", FenValue.FromFunction(new FenFunction("skipWaiting", (args, thisVal) =>
            {
                // TODO: Implement SW lifecycle skipWaiting
                // Return promise that resolves to undefined
                return FenValue.Undefined; 
            })));

            // 'clients' property (placeholder for now)
            Set("clients", FenValue.FromObject(new FenObject())); // Clients interface
            
            // 'registration' property (placeholder)
            Set("registration", FenValue.Null); 
        }
    }
}
