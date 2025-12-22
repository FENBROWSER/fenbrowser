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
        private readonly string _origin;
        private readonly Dictionary<string, Cache> _openCaches = new();

        public CacheStorage(string origin, IStorageBackend storageBackend)
        {
            _origin = origin;
            _storageBackend = storageBackend;

            Set("open", FenValue.FromFunction(new FenFunction("open", Open)));
            Set("has", FenValue.FromFunction(new FenFunction("has", Has)));
            Set("delete", FenValue.FromFunction(new FenFunction("delete", Delete)));
            Set("keys", FenValue.FromFunction(new FenFunction("keys", Keys)));
            Set("match", FenValue.FromFunction(new FenFunction("match", Match)));
        }

        private IValue Open(IValue[] args, IValue thisVal)
        {
             if (args.Length < 1) return FenValue.Undefined; // Should reject
             var cacheName = args[0].ToString();

             return FenValue.FromObject(CreatePromise(async () =>
             {
                 if (!_openCaches.ContainsKey(cacheName))
                 {
                     _openCaches[cacheName] = new Cache(_origin, cacheName, _storageBackend);
                 }
                 return FenValue.FromObject(_openCaches[cacheName]);
             }));
        }

        private IValue Has(IValue[] args, IValue thisVal)
        {
             if (args.Length < 1) return FenValue.FromBoolean(false);
             var cacheName = args[0].ToString();

             return FenValue.FromObject(CreatePromise(async () =>
             {
                 // Check if database exists for this cache
                 var info = await _storageBackend.GetDatabaseInfo(_origin, $"cache_{cacheName}");
                 return FenValue.FromBoolean(info != null);
             }));
        }

        private IValue Delete(IValue[] args, IValue thisVal)
        {
             if (args.Length < 1) return FenValue.FromBoolean(false);
             var cacheName = args[0].ToString();

             return FenValue.FromObject(CreatePromise(async () =>
             {
                 _openCaches.Remove(cacheName);
                 var deleted = await _storageBackend.GetDatabaseInfo(_origin, $"cache_{cacheName}") != null;
                 if (deleted)
                 {
                     await _storageBackend.DeleteDatabase(_origin, $"cache_{cacheName}");
                 }
                 return FenValue.FromBoolean(deleted);
             }));
        }

        private IValue Keys(IValue[] args, IValue thisVal)
        {
             return FenValue.FromObject(CreatePromise(async () =>
             {
                 // This requires listing all databases for origin and filtering by prefix "cache_"
                 // But IStorageBackend doesn't have ListDatabases method yet...
                 // TODO: Add ListDatabases to IStorageBackend or track caches in a meta-store
                 // For now, return empty list or track in memory
                 var list = new FenObject();
                 list.Set("length", FenValue.FromNumber(0));
                 return FenValue.FromObject(list);
             }));
        }

        private IValue Match(IValue[] args, IValue thisVal)
        {
            // Checks all caches for a match
             if (args.Length < 1) return FenValue.Undefined;
             
             // This is complex: need to iterate all caches. 
             // Without ListDatabases, we can only check open caches or known names.
             // Will satisfy interface but implementation limited for now.
             return FenValue.FromObject(CreatePromise(async () => FenValue.Undefined));
        }

        // --- Promise Helper (Dup from Cache.cs - should extract) ---
        private FenObject CreatePromise(Func<Task<IValue>> valueFactory)
        {
            var promise = new FenObject();
            Task.Run(async () =>
            {
                try
                {
                    var result = await valueFactory();
                    if (promise.Has("onFulfilled"))
                         promise.Get("onFulfilled").AsFunction()?.Invoke(new[] { result }, null);
                    else
                    {
                         promise.Set("__result", result);
                         promise.Set("__state", FenValue.FromString("fulfilled"));
                    }
                }
                catch (Exception ex)
                {
                     // Reject
                }
            });
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, _) =>
            {
                if (args.Length > 0) promise.Set("onFulfilled", args[0]);
                return FenValue.FromObject(promise);
            })));
            return promise;
        }
    }
}
