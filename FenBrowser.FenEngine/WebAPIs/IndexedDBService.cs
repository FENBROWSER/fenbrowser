using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.WebAPIs
{
    public class IndexedDBService : FenObject
    {
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<string, IValue>>> _databases = new();

        public static IValue Constructor(FenValue[] args, FenValue thisVal)
        {
            return FenValue.FromObject(new IndexedDBService());
        }

        public static void Register(IExecutionContext context)
        {
            var global = context.Environment;
            var indexedDB = new FenObject();
            
            indexedDB.Set("open", FenValue.FromFunction(new FenFunction("open", (args, thisVal) => 
            {
                string name = args.Length > 0 ? args[0].ToString() : "default";
                int version = args.Length > 1 ? (int)args[1].ToNumber() : 1;
                return OpenDatabase(name, version, context);
            })));

            global.Set("indexedDB", FenValue.FromObject(indexedDB));
        }

        private static FenValue OpenDatabase(string name, int version, IExecutionContext context)
        {
            var request = new FenObject();
            request.Set("onsuccess", FenValue.Null);
            request.Set("onerror", FenValue.Null);
            request.Set("onupgradeneeded", FenValue.Null);

            Task.Run(async () => 
            {
                await Task.Delay(10); // Simulate async
                
                var db = new FenObject();
                db.Set("name", FenValue.FromString(name));
                db.Set("version", FenValue.FromNumber(version));

                db.Set("createObjectStore", FenValue.FromFunction(new FenFunction("createObjectStore", (args, thisVal) => 
                {
                    string storeName = args.Length > 0 ? args[0].ToString() : "defaultStore";
                    return CreateObjectStore(name, storeName);
                })));

                db.Set("transaction", FenValue.FromFunction(new FenFunction("transaction", (args, thisVal) => 
                {
                    return CreateTransaction(name, args, context);
                })));

                // Trigger onsuccess
                var successCb = request.Get("onsuccess");
                if (successCb.IsFunction)
                {
                    var evt = new FenObject();
                    evt.Set("target", FenValue.FromObject(request));
                    request.Set("result", FenValue.FromObject(db));
                    successCb.AsFunction().Invoke(new[] { FenValue.FromObject(evt) }, context);
                }
            });

            return FenValue.FromObject(request);
        }

        private static FenValue CreateObjectStore(string dbName, string storeName)
        {
            var db = _databases.GetOrAdd(dbName, _ => new ConcurrentDictionary<string, ConcurrentDictionary<string, IValue>>());
            db.GetOrAdd(storeName, _ => new ConcurrentDictionary<string, IValue>());
            
            var store = new FenObject();
            store.Set("name", FenValue.FromString(storeName));
            return FenValue.FromObject(store);
        }

        private static FenValue CreateTransaction(string dbName, FenValue[] args, IExecutionContext context)
        {
            var trans = new FenObject();
            trans.Set("objectStore", FenValue.FromFunction(new FenFunction("objectStore", (osArgs, osThis) => 
            {
                string storeName = osArgs.Length > 0 ? osArgs[0].ToString() : "";
                return GetObjectStore(dbName, storeName);
            })));
            return FenValue.FromObject(trans);
        }

        private static FenValue GetObjectStore(string dbName, string storeName)
        {
            var os = new FenObject();
            os.Set("add", FenValue.FromFunction(new FenFunction("add", (args, thisVal) => 
            {
                if (args.Length < 1) return FenValue.Null;
                var value = args[0];
                var key = args.Length > 1 ? args[1].ToString() : Guid.NewGuid().ToString();
                
                var db = _databases.GetOrAdd(dbName, _ => new ConcurrentDictionary<string, ConcurrentDictionary<string, IValue>>());
                var store = db.GetOrAdd(storeName, _ => new ConcurrentDictionary<string, IValue>());
                store[key] = value;
                
                return CreateRequest(FenValue.FromString(key));
            })));

            os.Set("get", FenValue.FromFunction(new FenFunction("get", (args, thisVal) => 
            {
                if (args.Length < 1) return FenValue.Null;
                var key = args[0].ToString();
                
                var db = _databases.GetOrAdd(dbName, _ => new ConcurrentDictionary<string, ConcurrentDictionary<string, IValue>>());
                var store = db.GetOrAdd(storeName, _ => new ConcurrentDictionary<string, IValue>());
                
                store.TryGetValue(key, out var val);
                return CreateRequest((FenValue)val );
            })));

            return FenValue.FromObject(os);
        }

        private static FenValue CreateRequest(FenValue result)
        {
            var req = new FenObject();
            req.Set("result", result);
            return FenValue.FromObject(req);
        }
    }
}
