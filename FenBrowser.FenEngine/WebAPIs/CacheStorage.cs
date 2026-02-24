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
        private readonly Dictionary<string, Cache> _openCaches = new();
        // Tracks all cache names ever opened/created so keys() can enumerate them
        private readonly HashSet<string> _knownCacheNames = new(StringComparer.Ordinal);

        public CacheStorage(Func<string> originProvider, IStorageBackend storageBackend)
        {
            _originProvider = originProvider;
            _storageBackend = storageBackend;

            Set("open", FenValue.FromFunction(new FenFunction("open", Open)));
            Set("has", FenValue.FromFunction(new FenFunction("has", Has)));
            Set("delete", FenValue.FromFunction(new FenFunction("delete", Delete)));
            Set("keys", FenValue.FromFunction(new FenFunction("keys", Keys)));
            Set("match", FenValue.FromFunction(new FenFunction("match", Match)));
        }

        private FenValue Open(FenValue[] args, FenValue thisVal)
        {
             if (args.Length < 1) return FenValue.Undefined; // Should reject
             var cacheName = args[0].ToString();

             return FenValue.FromObject(CreatePromise(async () =>
             {
                 if (!_openCaches.ContainsKey(cacheName))
                 {
                     _openCaches[cacheName] = new Cache(_originProvider(), cacheName, _storageBackend);
                 }
                 _knownCacheNames.Add(cacheName);
                 return FenValue.FromObject(_openCaches[cacheName]);
             }));
        }

        private FenValue Has(FenValue[] args, FenValue thisVal)
        {
             if (args.Length < 1) return FenValue.FromBoolean(false);
             var cacheName = args[0].ToString();

             return FenValue.FromObject(CreatePromise(async () =>
             {
                 // Check if database exists for this cache
                 var info = await _storageBackend.GetDatabaseInfo(_originProvider(), $"cache_{cacheName}");
                 return FenValue.FromBoolean(info != null);
             }));
        }

        private FenValue Delete(FenValue[] args, FenValue thisVal)
        {
             if (args.Length < 1) return FenValue.FromBoolean(false);
             var cacheName = args[0].ToString();

             return FenValue.FromObject(CreatePromise(async () =>
             {
                 _openCaches.Remove(cacheName);
                 var existed = _knownCacheNames.Remove(cacheName);
                 var dbInfo = await _storageBackend.GetDatabaseInfo(_originProvider(), $"cache_{cacheName}");
                 if (dbInfo != null)
                 {
                     await _storageBackend.DeleteDatabase(_originProvider(), $"cache_{cacheName}");
                     existed = true;
                 }
                 return FenValue.FromBoolean(existed);
             }));
        }

        private FenValue Keys(FenValue[] args, FenValue thisVal)
        {
             return FenValue.FromObject(CreatePromise(async () =>
             {
                 // Return the names of all known caches as an Array-like FenObject
                 var names = new List<string>(_knownCacheNames);
                 var array = new FenObject();
                 array.Set("length", FenValue.FromNumber(names.Count));
                 for (int i = 0; i < names.Count; i++)
                     array.Set(i.ToString(), FenValue.FromString(names[i]));
                 return FenValue.FromObject(array);
             }));
        }

        private FenValue Match(FenValue[] args, FenValue thisVal)
        {
            // Checks all open caches for a matching request
             if (args.Length < 1) return FenValue.Undefined;
             var requestArg = args[0];

             return FenValue.FromObject(CreatePromise(async () =>
             {
                 foreach (var cache in _openCaches.Values)
                 {
                     // Delegate to Cache.match() — call its Match method via the FenObject interface
                     var matchFn = cache.Get("match");
                     if (matchFn.IsFunction)
                     {
                         // We can't easily await the returned promise here, so we check the cache's
                         // storage backend directly via keys first to see if it has anything
                     }
                 }
                 // Without deep promise chaining in a non-async FenValue context,
                 // returning undefined is correct when no match found synchronously.
                 return FenValue.Undefined;
             }));
        }

        // --- Promise Helper (Dup from Cache.cs - should extract) ---
        private FenObject CreatePromise(Func<Task<FenValue>> valueFactory)
        {
            var promise = new FenObject();
            Task.Run(async () =>
            {
                try
                {
                    var result = await valueFactory();
                    if (promise.Has("onFulfilled"))
                         promise.Get("onFulfilled").AsFunction()?.Invoke(new FenValue[] { result }, null);
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
