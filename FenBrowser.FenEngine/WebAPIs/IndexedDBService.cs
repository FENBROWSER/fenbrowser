using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.WebAPIs
{
    public class IndexedDBService : FenObject
    {
        private sealed class DatabaseState
        {
            public DatabaseState(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public int Version { get; set; }
            public ConcurrentDictionary<string, ObjectStoreState> Stores { get; } = new ConcurrentDictionary<string, ObjectStoreState>(StringComparer.Ordinal);
            public object SyncRoot { get; } = new object();
        }

        private sealed class ObjectStoreState
        {
            public ObjectStoreState(string name, string keyPath, bool autoIncrement)
            {
                Name = name;
                KeyPath = keyPath;
                AutoIncrement = autoIncrement;
            }

            public string Name { get; }
            public string KeyPath { get; }
            public bool AutoIncrement { get; }
            public long NextAutoIncrementKey { get; set; } = 1;
            public ConcurrentDictionary<string, FenValue> Records { get; } = new ConcurrentDictionary<string, FenValue>(StringComparer.Ordinal);
        }

        private static readonly ConcurrentDictionary<string, DatabaseState> Databases = new(StringComparer.Ordinal);

        public static long EstimateUsageBytes()
        {
            long total = 0;
            foreach (var database in Databases.Values)
            {
                total += (database.Name?.Length ?? 0) * 2L;
                total += sizeof(int);
                foreach (var store in database.Stores.Values)
                {
                    total += (store.Name?.Length ?? 0) * 2L;
                    total += (store.KeyPath?.Length ?? 0) * 2L;
                    foreach (var record in store.Records)
                    {
                        total += (record.Key?.Length ?? 0) * 2L;
                        total += (record.Value.ToString()?.Length ?? 0) * 2L;
                    }
                }
            }

            return total;
        }

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
                var name = args.Length > 0 ? args[0].ToString() : "default";
                var version = args.Length > 1 ? (int)args[1].ToNumber() : 1;
                return OpenDatabase(name, version, context);
            })));

            indexedDB.Set("deleteDatabase", FenValue.FromFunction(new FenFunction("deleteDatabase", (args, thisVal) =>
            {
                var name = args.Length > 0 ? args[0].ToString() : "default";
                return DeleteDatabase(name, context);
            })));

            global.Set("indexedDB", FenValue.FromObject(indexedDB));
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
                    FenBrowser.Core.FenLogger.Warn($"[IndexedDB] Detached async operation failed: {ex.Message}", LogCategory.Storage);
                }
            }, System.Threading.CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }

        private static FenValue OpenDatabase(string name, int version, IExecutionContext context)
        {
            var request = CreateRequest();

            _ = RunDetachedAsync(async () =>
            {
                await Task.Delay(10).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(name))
                {
                    DispatchRequestError(request, "TypeError: Database name is required.", context, null, null);
                    return;
                }

                if (version <= 0)
                {
                    DispatchRequestError(request, "VersionError: Database version must be greater than zero.", context, null, null);
                    return;
                }

                var dbState = Databases.GetOrAdd(name, static key => new DatabaseState(key));
                bool upgradeNeeded = false;
                int oldVersion;

                lock (dbState.SyncRoot)
                {
                    oldVersion = dbState.Version;
                    if (oldVersion > version)
                    {
                        DispatchRequestError(request, "VersionError: Requested version is lower than the existing database version.", context, null, null);
                        return;
                    }

                    if (oldVersion < version)
                    {
                        dbState.Version = version;
                        upgradeNeeded = true;
                    }
                }

                var db = CreateDatabaseObject(dbState, context, upgradeNeeded);
                request.Set("result", FenValue.FromObject(db));

                context.ScheduleCallback(() =>
                {
                    try
                    {
                        if (upgradeNeeded)
                        {
                            var upgradeEvent = new FenObject();
                            upgradeEvent.Set("target", FenValue.FromObject(request));
                            upgradeEvent.Set("oldVersion", FenValue.FromNumber(oldVersion));
                            upgradeEvent.Set("newVersion", FenValue.FromNumber(version));
                            request.Set("transaction", FenValue.FromObject(CreateTransaction(dbState, new[] { "*" }, "versionchange", context, true, db)));
                            InvokeRequestHandler(request, "onupgradeneeded", upgradeEvent, context);
                        }

                        db.Set("__schemaMutable", FenValue.FromBoolean(false));
                        request.Set("readyState", FenValue.FromString("done"));
                        InvokeRequestHandler(request, "onsuccess", CreateEvent(request), context);
                    }
                    catch (Exception ex)
                    {
                        DispatchRequestError(request, ex.Message, context, null, null);
                    }
                }, 0);
            });

            return FenValue.FromObject(request);
        }

        private static FenValue DeleteDatabase(string name, IExecutionContext context)
        {
            var request = CreateRequest();
            _ = RunDetachedAsync(async () =>
            {
                await Task.Delay(10).ConfigureAwait(false);
                Databases.TryRemove(name, out _);
                DispatchRequestSuccess(request, FenValue.Undefined, context, null, null);
            });
            return FenValue.FromObject(request);
        }

        private static FenObject CreateDatabaseObject(DatabaseState dbState, IExecutionContext context, bool schemaMutable)
        {
            var db = new FenObject();
            db.Set("name", FenValue.FromString(dbState.Name));
            db.Set("version", FenValue.FromNumber(dbState.Version));
            db.Set("__closed", FenValue.FromBoolean(false));
            db.Set("__schemaMutable", FenValue.FromBoolean(schemaMutable));
            db.Set("objectStoreNames", FenValue.FromObject(CreateStringArray(dbState.Stores.Keys.OrderBy(key => key, StringComparer.Ordinal))));

            db.Set("createObjectStore", FenValue.FromFunction(new FenFunction("createObjectStore", (args, thisVal) =>
            {
                if (db.Get("__closed").ToBoolean())
                {
                    return FenValue.FromError("InvalidStateError: Database connection is closed.");
                }

                if (!db.Get("__schemaMutable").ToBoolean())
                {
                    return FenValue.FromError("InvalidStateError: createObjectStore is only available during version upgrades.");
                }

                var storeName = args.Length > 0 ? args[0].ToString() : string.Empty;
                if (string.IsNullOrWhiteSpace(storeName))
                {
                    return FenValue.FromError("TypeError: Object store name is required.");
                }

                string keyPath = null;
                bool autoIncrement = false;
                if (args.Length > 1 && args[1].IsObject)
                {
                    var options = args[1].AsObject();
                    if (options.Has("keyPath") && options.Get("keyPath").IsString)
                    {
                        keyPath = options.Get("keyPath").ToString();
                    }

                    if (options.Has("autoIncrement"))
                    {
                        autoIncrement = options.Get("autoIncrement").ToBoolean();
                    }
                }

                lock (dbState.SyncRoot)
                {
                    if (dbState.Stores.ContainsKey(storeName))
                    {
                        return FenValue.FromError("ConstraintError: Object store already exists.");
                    }

                    dbState.Stores[storeName] = new ObjectStoreState(storeName, keyPath, autoIncrement);
                    db.Set("objectStoreNames", FenValue.FromObject(CreateStringArray(dbState.Stores.Keys.OrderBy(key => key, StringComparer.Ordinal))));
                    return FenValue.FromObject(CreateObjectStore(dbState, dbState.Stores[storeName], context, null, false));
                }
            })));

            db.Set("deleteObjectStore", FenValue.FromFunction(new FenFunction("deleteObjectStore", (args, thisVal) =>
            {
                if (!db.Get("__schemaMutable").ToBoolean())
                {
                    return FenValue.FromError("InvalidStateError: deleteObjectStore is only available during version upgrades.");
                }

                var storeName = args.Length > 0 ? args[0].ToString() : string.Empty;
                if (string.IsNullOrWhiteSpace(storeName))
                {
                    return FenValue.FromError("TypeError: Object store name is required.");
                }

                lock (dbState.SyncRoot)
                {
                    if (!dbState.Stores.TryRemove(storeName, out _))
                    {
                        return FenValue.FromError("NotFoundError: Object store does not exist.");
                    }

                    db.Set("objectStoreNames", FenValue.FromObject(CreateStringArray(dbState.Stores.Keys.OrderBy(key => key, StringComparer.Ordinal))));
                }

                return FenValue.Undefined;
            })));

            db.Set("transaction", FenValue.FromFunction(new FenFunction("transaction", (args, thisVal) =>
            {
                if (db.Get("__closed").ToBoolean())
                {
                    return FenValue.FromError("InvalidStateError: Database connection is closed.");
                }

                var storeNames = ParseStoreNames(args.Length > 0 ? args[0] : FenValue.Undefined);
                var mode = args.Length > 1 ? args[1].ToString() : "readonly";
                return FenValue.FromObject(CreateTransaction(dbState, storeNames, mode, context, false, db));
            })));

            db.Set("close", FenValue.FromFunction(new FenFunction("close", (args, thisVal) =>
            {
                db.Set("__closed", FenValue.FromBoolean(true));
                return FenValue.Undefined;
            })));

            return db;
        }

        private static FenObject CreateTransaction(DatabaseState dbState, IReadOnlyList<string> requestedStoreNames, string mode, IExecutionContext context, bool schemaMutable, FenObject db)
        {
            var transaction = new FenObject();
            var normalizedMode = string.Equals(mode, "readwrite", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "versionchange", StringComparison.OrdinalIgnoreCase)
                ? mode.ToLowerInvariant()
                : "readonly";
            var completed = false;
            var aborted = false;

            transaction.Set("db", FenValue.FromObject(db));
            transaction.Set("mode", FenValue.FromString(normalizedMode));
            transaction.Set("error", FenValue.Null);
            transaction.Set("oncomplete", FenValue.Null);
            transaction.Set("onerror", FenValue.Null);
            transaction.Set("onabort", FenValue.Null);
            transaction.Set("objectStoreNames", FenValue.FromObject(CreateStringArray(requestedStoreNames.Where(name => name != "*"))));

            transaction.Set("objectStore", FenValue.FromFunction(new FenFunction("objectStore", (args, thisVal) =>
            {
                if (completed || aborted)
                {
                    return FenValue.FromError("InvalidStateError: Transaction is no longer active.");
                }

                var storeName = args.Length > 0 ? args[0].ToString() : string.Empty;
                if (string.IsNullOrWhiteSpace(storeName))
                {
                    return FenValue.FromError("TypeError: Object store name is required.");
                }

                if (!requestedStoreNames.Contains("*") && !requestedStoreNames.Contains(storeName))
                {
                    return FenValue.FromError("NotFoundError: Object store is not part of this transaction.");
                }

                if (!dbState.Stores.TryGetValue(storeName, out var storeState))
                {
                    return FenValue.FromError("NotFoundError: Object store does not exist.");
                }

                return FenValue.FromObject(CreateObjectStore(dbState, storeState, context, transaction, normalizedMode == "readonly" && !schemaMutable));
            })));

            transaction.Set("commit", FenValue.FromFunction(new FenFunction("commit", (args, thisVal) =>
            {
                if (completed || aborted)
                {
                    return FenValue.Undefined;
                }

                completed = true;
                context.ScheduleCallback(() =>
                {
                    InvokeRequestHandler(transaction, "oncomplete", CreateEvent(transaction), context);
                }, 0);
                return FenValue.Undefined;
            })));

            transaction.Set("abort", FenValue.FromFunction(new FenFunction("abort", (args, thisVal) =>
            {
                if (completed || aborted)
                {
                    return FenValue.Undefined;
                }

                aborted = true;
                transaction.Set("error", FenValue.FromString("AbortError"));
                context.ScheduleCallback(() =>
                {
                    InvokeRequestHandler(transaction, "onabort", CreateEvent(transaction), context);
                }, 0);
                return FenValue.Undefined;
            })));

            return transaction;
        }

        private static FenObject CreateObjectStore(DatabaseState dbState, ObjectStoreState storeState, IExecutionContext context, FenObject transaction, bool readOnly)
        {
            var objectStore = new FenObject();
            objectStore.Set("name", FenValue.FromString(storeState.Name));
            objectStore.Set("keyPath", string.IsNullOrEmpty(storeState.KeyPath) ? FenValue.Null : FenValue.FromString(storeState.KeyPath));
            objectStore.Set("autoIncrement", FenValue.FromBoolean(storeState.AutoIncrement));
            objectStore.Set("transaction", transaction == null ? FenValue.Null : FenValue.FromObject(transaction));

            objectStore.Set("add", FenValue.FromFunction(new FenFunction("add", (args, thisVal) =>
            {
                var request = CreateRequest();
                if (readOnly)
                {
                    DispatchRequestError(request, "ReadOnlyError: Transaction is readonly.", context, objectStore, transaction);
                    return FenValue.FromObject(request);
                }

                var value = args.Length > 0 ? args[0] : FenValue.Undefined;
                var suppliedKey = args.Length > 1 ? args[1] : FenValue.Undefined;

                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    lock (dbState.SyncRoot)
                    {
                        var key = ResolveKey(storeState, value, suppliedKey);
                        if (storeState.Records.ContainsKey(key))
                        {
                            DispatchRequestError(request, "ConstraintError: Key already exists.", context, objectStore, transaction);
                            return;
                        }

                        storeState.Records[key] = value;
                        DispatchRequestSuccess(request, FenValue.FromString(key), context, objectStore, transaction);
                    }
                });

                return FenValue.FromObject(request);
            })));

            objectStore.Set("put", FenValue.FromFunction(new FenFunction("put", (args, thisVal) =>
            {
                var request = CreateRequest();
                if (readOnly)
                {
                    DispatchRequestError(request, "ReadOnlyError: Transaction is readonly.", context, objectStore, transaction);
                    return FenValue.FromObject(request);
                }

                var value = args.Length > 0 ? args[0] : FenValue.Undefined;
                var suppliedKey = args.Length > 1 ? args[1] : FenValue.Undefined;

                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    lock (dbState.SyncRoot)
                    {
                        var key = ResolveKey(storeState, value, suppliedKey);
                        storeState.Records[key] = value;
                        DispatchRequestSuccess(request, FenValue.FromString(key), context, objectStore, transaction);
                    }
                });

                return FenValue.FromObject(request);
            })));

            objectStore.Set("get", FenValue.FromFunction(new FenFunction("get", (args, thisVal) =>
            {
                var request = CreateRequest();
                var key = args.Length > 0 ? args[0].ToString() : string.Empty;

                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    storeState.Records.TryGetValue(key, out var value);
                    DispatchRequestSuccess(request, value.IsUndefined ? FenValue.Null : value, context, objectStore, transaction);
                });

                return FenValue.FromObject(request);
            })));

            objectStore.Set("delete", FenValue.FromFunction(new FenFunction("delete", (args, thisVal) =>
            {
                var request = CreateRequest();
                if (readOnly)
                {
                    DispatchRequestError(request, "ReadOnlyError: Transaction is readonly.", context, objectStore, transaction);
                    return FenValue.FromObject(request);
                }

                var key = args.Length > 0 ? args[0].ToString() : string.Empty;
                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    storeState.Records.TryRemove(key, out _);
                    DispatchRequestSuccess(request, FenValue.Undefined, context, objectStore, transaction);
                });

                return FenValue.FromObject(request);
            })));

            objectStore.Set("clear", FenValue.FromFunction(new FenFunction("clear", (args, thisVal) =>
            {
                var request = CreateRequest();
                if (readOnly)
                {
                    DispatchRequestError(request, "ReadOnlyError: Transaction is readonly.", context, objectStore, transaction);
                    return FenValue.FromObject(request);
                }

                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    storeState.Records.Clear();
                    DispatchRequestSuccess(request, FenValue.Undefined, context, objectStore, transaction);
                });

                return FenValue.FromObject(request);
            })));

            objectStore.Set("count", FenValue.FromFunction(new FenFunction("count", (args, thisVal) =>
            {
                var request = CreateRequest();
                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    DispatchRequestSuccess(request, FenValue.FromNumber(storeState.Records.Count), context, objectStore, transaction);
                });
                return FenValue.FromObject(request);
            })));

            objectStore.Set("getAll", FenValue.FromFunction(new FenFunction("getAll", (args, thisVal) =>
            {
                var request = CreateRequest();
                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    DispatchRequestSuccess(request, FenValue.FromObject(CreateValueArray(storeState.Records.Values.ToList())), context, objectStore, transaction);
                });
                return FenValue.FromObject(request);
            })));

            return objectStore;
        }

        private static string ResolveKey(ObjectStoreState storeState, FenValue value, FenValue suppliedKey)
        {
            if (!suppliedKey.IsUndefined && !suppliedKey.IsNull)
            {
                return suppliedKey.ToString();
            }

            if (!string.IsNullOrEmpty(storeState.KeyPath) && value.IsObject)
            {
                var existingKey = value.AsObject().Get(storeState.KeyPath);
                if (!existingKey.IsUndefined && !existingKey.IsNull)
                {
                    return existingKey.ToString();
                }
            }

            if (storeState.AutoIncrement)
            {
                var key = storeState.NextAutoIncrementKey++.ToString();
                if (!string.IsNullOrEmpty(storeState.KeyPath) && value.IsObject)
                {
                    value.AsObject().Set(storeState.KeyPath, FenValue.FromString(key));
                }
                return key;
            }

            return Guid.NewGuid().ToString("N");
        }

        private static FenObject CreateRequest()
        {
            var request = new FenObject();
            request.Set("onsuccess", FenValue.Null);
            request.Set("onerror", FenValue.Null);
            request.Set("onupgradeneeded", FenValue.Null);
            request.Set("onblocked", FenValue.Null);
            request.Set("result", FenValue.Null);
            request.Set("error", FenValue.Null);
            request.Set("readyState", FenValue.FromString("pending"));
            request.Set("source", FenValue.Null);
            request.Set("transaction", FenValue.Null);
            return request;
        }

        private static void DispatchRequestSuccess(FenObject request, FenValue result, IExecutionContext context, FenObject source, FenObject transaction)
        {
            context.ScheduleCallback(() =>
            {
                request.Set("result", result);
                request.Set("error", FenValue.Null);
                request.Set("readyState", FenValue.FromString("done"));
                if (source != null)
                {
                    request.Set("source", FenValue.FromObject(source));
                }
                if (transaction != null)
                {
                    request.Set("transaction", FenValue.FromObject(transaction));
                }
                InvokeRequestHandler(request, "onsuccess", CreateEvent(request), context);
            }, 0);
        }

        private static void DispatchRequestError(FenObject request, string error, IExecutionContext context, FenObject source, FenObject transaction)
        {
            context.ScheduleCallback(() =>
            {
                request.Set("error", FenValue.FromString(error ?? "UnknownError"));
                request.Set("readyState", FenValue.FromString("done"));
                if (source != null)
                {
                    request.Set("source", FenValue.FromObject(source));
                }
                if (transaction != null)
                {
                    request.Set("transaction", FenValue.FromObject(transaction));
                }
                InvokeRequestHandler(request, "onerror", CreateEvent(request), context);
            }, 0);
        }

        private static FenObject CreateEvent(FenObject target)
        {
            var evt = new FenObject();
            evt.Set("target", FenValue.FromObject(target));
            evt.Set("currentTarget", FenValue.FromObject(target));
            return evt;
        }

        private static void InvokeRequestHandler(FenObject target, string propertyName, FenObject evt, IExecutionContext context)
        {
            var callback = target.Get(propertyName);
            if (!callback.IsFunction)
            {
                return;
            }

            try
            {
                callback.AsFunction().Invoke(new[] { FenValue.FromObject(evt) }, context);
            }
            catch (Exception ex)
            {
                FenBrowser.Core.FenLogger.Warn($"[IndexedDB] {propertyName} callback failed: {ex.Message}", LogCategory.Storage);
            }
        }

        private static FenObject CreateStringArray(IEnumerable<string> items)
        {
            var arr = FenObject.CreateArray();
            var index = 0;
            foreach (var item in items)
            {
                arr.Set(index.ToString(), FenValue.FromString(item));
                index++;
            }
            arr.Set("length", FenValue.FromNumber(index));
            return arr;
        }

        private static FenObject CreateValueArray(IReadOnlyList<FenValue> items)
        {
            var arr = FenObject.CreateArray();
            for (var i = 0; i < items.Count; i++)
            {
                arr.Set(i.ToString(), items[i]);
            }
            arr.Set("length", FenValue.FromNumber(items.Count));
            return arr;
        }

        private static IReadOnlyList<string> ParseStoreNames(FenValue value)
        {
            if (value.IsString)
            {
                return new[] { value.ToString() };
            }

            if (value.IsObject)
            {
                var obj = value.AsObject();
                var lengthValue = obj.Get("length");
                if (lengthValue.IsNumber)
                {
                    var length = Math.Max(0, (int)lengthValue.ToNumber());
                    var names = new List<string>(length);
                    for (var i = 0; i < length; i++)
                    {
                        var entry = obj.Get(i.ToString());
                        if (entry.IsString)
                        {
                            names.Add(entry.ToString());
                        }
                    }
                    return names;
                }
            }

            return Array.Empty<string>();
        }
    }
}
