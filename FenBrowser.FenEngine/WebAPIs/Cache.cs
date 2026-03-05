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
    /// Represents the Cache interface (Service Worker Cache API).
    /// Stores Request/Response pairs.
    /// </summary>
    public class Cache : FenObject
    {
        private readonly string _cacheName;
        private readonly string _origin;
        private readonly IStorageBackend _storage;
        private const string STORE_NAME = "cache_entries";

        public Cache(string origin, string cacheName, IStorageBackend storage)
        {
            _origin = origin;
            _cacheName = cacheName;
            _storage = storage;

            InitialiseInterface();
            _ = InitializeStorage(); // Fire and forget init, discard task
        }

        private async Task InitializeStorage()
        {
            try 
            {
                await _storage.OpenDatabase(_origin, $"cache_{_cacheName}", 1);
                // Schema: Key = Request URL, Value = CacheEntry (JSON)
                await _storage.CreateObjectStore(_origin, $"cache_{_cacheName}", STORE_NAME, new ObjectStoreOptions { KeyPath = "url" });
            }
            catch (Exception ex)
            {
                // Likely already exists or open
                FenBrowser.Core.FenLogger.Debug($"[Cache] Init: {ex.Message}", LogCategory.Storage);
            }
        }

        private void InitialiseInterface()
        {
            Set("match", FenValue.FromFunction(new FenFunction("match", Match)));
            Set("put", FenValue.FromFunction(new FenFunction("put", Put)));
            Set("delete", FenValue.FromFunction(new FenFunction("delete", Delete)));
            Set("keys", FenValue.FromFunction(new FenFunction("keys", Keys)));
        }

        private FenValue Match(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1) return FenValue.FromObject(CreatePromise(() => Resolve(FenValue.Undefined)));

            var requestUrl = ResolveUrl(args[0]);

            return FenValue.FromObject(CreatePromise(async () =>
            {
                var entry = await _storage.Get(_origin, $"cache_{_cacheName}", STORE_NAME, requestUrl);
                if (entry is CacheEntry cached)
                {
                    return CreateJsResponse(cached);
                }
                return FenValue.Undefined;
            }));
        }

        private FenValue Put(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2) return FenValue.Undefined; // Should reject

            var requestUrl = ResolveUrl(args[0]);
            var responseObj = args[1].AsObject(); 

            return FenValue.FromObject(CreatePromise(async () =>
            {
                var entry = await SerializeResponse(requestUrl, responseObj);
                await _storage.Put(_origin, $"cache_{_cacheName}", STORE_NAME, requestUrl, entry);
                return FenValue.Undefined;
            }));
        }

        private FenValue Delete(FenValue[] args, FenValue thisVal)
        {
             if (args.Length < 1) return FenValue.FromObject(CreatePromise(() => Resolve(FenValue.FromBoolean(false))));

             var requestUrl = ResolveUrl(args[0]);

             return FenValue.FromObject(CreatePromise(async () => {
                 var exists = await _storage.Get(_origin, $"cache_{_cacheName}", STORE_NAME, requestUrl) != null;
                 if (exists)
                 {
                     await _storage.Delete(_origin, $"cache_{_cacheName}", STORE_NAME, requestUrl);
                 }
                 return FenValue.FromBoolean(exists);
             }));
        }

        private FenValue Keys(FenValue[] args, FenValue thisVal)
        {
            return FenValue.FromObject(CreatePromise(async () =>
            {
                var keys = await _storage.GetAllKeys(_origin, $"cache_{_cacheName}", STORE_NAME);
                var list = new List<FenValue>();
                foreach(var k in keys)
                {
                    var req = new FenObject();
                    req.Set("url", FenValue.FromString(k.ToString()));
                    list.Add(FenValue.FromObject(req));
                }
                
                var array = new FenObject();
                array.Set("length", FenValue.FromNumber(list.Count));
                for(int i=0; i<list.Count; i++) array.Set(i.ToString(), list[i]);
                
                return FenValue.FromObject(array);
            }));
        }

        #region Helpers

        private string ResolveUrl(IValue request)
        {
            if (request.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.String) return request.ToString();
            if (request.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Object)
            {
                var obj = request.AsObject(); 
                if (request is FenValue fv)
                {
                    obj = fv.AsObject();
                }
                else
                {
                    obj = ((dynamic)request).AsObject();
                    // Or check manual interface property if needed
                }
                
                if (obj != null && obj.Has("url"))
                {
                     var u = obj.Get("url");
                     if (u != null) return u.ToString();
                }
            }
            return request.ToString();
        }

        private Task<FenValue> Resolve(FenValue value) => Task.FromResult(value);

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
                    FenBrowser.Core.FenLogger.Warn($"[Cache] Detached async operation failed: {ex.Message}", LogCategory.Storage);
                }
            }, System.Threading.CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }
        private FenObject CreatePromise(Func<Task<FenValue>> valueFactory)
        {
            var promise = new FenObject();
            _ = RunDetachedAsync(async () =>
            {
                try
                {
                    var result = await valueFactory();
                    ResolvePromise(promise, result);
                }
                catch (Exception ex)
                {
                    RejectPromise(promise, ex.Message);
                }
            });
            
            SetupPromiseThen(promise);
            return promise;
        }

        private void ResolvePromise(FenObject promise, FenValue result)
        {
             if (promise.Has("onFulfilled"))
             {
                 var cb = promise.Get("onFulfilled").AsFunction();
                 cb?.Invoke(new[] { result }, null);
             }
             else
             {
                 promise.Set("__result", result);
                 promise.Set("__state", FenValue.FromString("fulfilled"));
             }
        }

        private void RejectPromise(FenObject promise, string error)
        {
             if (promise.Has("onRejected"))
             {
                 var cb = promise.Get("onRejected").AsFunction();
                 cb?.Invoke(new[] { FenValue.FromString(error) }, null);
             }
             else
             {
                 promise.Set("__reason", FenValue.FromString(error));
                 promise.Set("__state", FenValue.FromString("rejected"));
             }
        }

        private void SetupPromiseThen(FenObject promise)
        {
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, _) =>
            {
                if (args.Length > 0) promise.Set("onFulfilled", args[0]);
                if (args.Length > 1) promise.Set("onRejected", args[1]);

                var state = promise.Get("__state").ToString();
                if (state == "fulfilled")
                {
                    var res = promise.Get("__result");
                    var cb = args[0];
                    if (cb.IsFunction) cb.AsFunction().Invoke(new[] { res }, null);
                }
                else if (state == "rejected")
                {
                     var reason = promise.Get("__reason");
                     var cb = args[1];
                     if (cb.IsFunction) cb.AsFunction().Invoke(new[] { reason }, null);
                }

                return FenValue.FromObject(promise); 
            })));
        }

        private async Task<CacheEntry> SerializeResponse(string url, IObject response)
        {
            var statusVal = response.Get("status");
            var status = (int)(statusVal.IsNumber ? statusVal.ToNumber() : 200); 
            
            var statusTextVal = response.Get("statusText");
            var statusText = statusTextVal.IsString ? statusTextVal.ToString() : "OK";
            
            string body = "Cached Body content placeholder";
            
            return new CacheEntry
            {
                Url = url,
                Status = status,
                StatusText = statusText,
                Body = body,
                Headers = new Dictionary<string,string>() 
            };
        }

        private FenValue CreateJsResponse(CacheEntry entry)
        {
            var res = new FenObject();
            res.Set("status", FenValue.FromNumber(entry.Status));
            res.Set("statusText", FenValue.FromString(entry.StatusText));
            res.Set("ok", FenValue.FromBoolean(entry.Status >= 200 && entry.Status < 300));
            res.Set("url", FenValue.FromString(entry.Url));
            
            res.Set("text", FenValue.FromFunction(new FenFunction("text", (a,t) => 
               FenValue.FromObject(CreatePromise(() => Resolve(FenValue.FromString(entry.Body)))))));
               
            res.Set("json", FenValue.FromFunction(new FenFunction("json", (a,t) => 
               FenValue.FromObject(CreatePromise(() => Resolve(FenValue.FromString(entry.Body))))))); 

            return FenValue.FromObject(res);
        }

        #endregion
    }

    public class CacheEntry
    {
        public string Url { get; set; }
        public int Status { get; set; }
        public string StatusText { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public string Body { get; set; }
    }
}

