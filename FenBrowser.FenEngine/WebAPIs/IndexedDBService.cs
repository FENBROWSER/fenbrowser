using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Storage;

namespace FenBrowser.FenEngine.WebAPIs
{
    public class IndexedDBService : FenObject
    {
        // IDB §2.1: Configurable storage backend for persistence.
        // When set, records are loaded on open and flushed on transaction commit.
        private static IStorageBackend _storageBackend;
        private static string _defaultOrigin = "null";

        /// <summary>
        /// Configure the storage backend for IndexedDB persistence (IDB §2.11).
        /// When null (default), IndexedDB operates in-memory only.
        /// </summary>
        public static void SetStorageBackend(IStorageBackend backend)
        {
            _storageBackend = backend;
        }

        private sealed class DatabaseState
        {
            public DatabaseState(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public string Origin { get; set; } = "null";
            public int Version { get; set; }
            public ConcurrentDictionary<string, ObjectStoreState> Stores { get; } = new ConcurrentDictionary<string, ObjectStoreState>(StringComparer.Ordinal);
            public object SyncRoot { get; } = new object();
            public bool Dirty { get; set; }
        }

        private sealed class IndexState
        {
            public IndexState(string name, string keyPath, bool unique, bool multiEntry)
            {
                Name = name;
                KeyPath = keyPath;
                Unique = unique;
                MultiEntry = multiEntry;
            }

            public string Name { get; }
            public string KeyPath { get; }
            public bool Unique { get; }
            public bool MultiEntry { get; }
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
            public ConcurrentDictionary<string, IndexState> Indexes { get; } = new ConcurrentDictionary<string, IndexState>(StringComparer.Ordinal);
        }

        private static readonly ConcurrentDictionary<string, DatabaseState> Databases = new(StringComparer.Ordinal);
        // IDB §2.2.1: Track databases with active versionchange transactions for blocked event dispatch
        private static readonly ConcurrentDictionary<string, bool> _activeVersionchangeDb = new(StringComparer.Ordinal);

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

        public static void Register(IExecutionContext context, string origin = null, IStorageBackend backend = null)
        {
            if (backend != null) _storageBackend = backend;
            if (origin != null) _defaultOrigin = origin;

            var global = context.Environment;
            var indexedDB = new FenObject();
            var effectiveOrigin = origin ?? _defaultOrigin;

            indexedDB.Set("open", FenValue.FromFunction(new FenFunction("open", (args, thisVal) =>
            {
                var name = args.Length > 0 ? args[0].ToString() : "default";
                var version = args.Length > 1 ? (int)args[1].ToNumber() : 1;
                return OpenDatabase(name, version, context, effectiveOrigin);
            })));

            indexedDB.Set("deleteDatabase", FenValue.FromFunction(new FenFunction("deleteDatabase", (args, thisVal) =>
            {
                var name = args.Length > 0 ? args[0].ToString() : "default";
                return DeleteDatabase(name, context, effectiveOrigin);
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

        /// <summary>
        /// Gets the origin-partitioned database key per IDB §4.1.
        /// </summary>
        private static string GetDbKey(string origin, string name) => $"{origin}|{name}";

        private static FenValue OpenDatabase(string name, int version, IExecutionContext context, string origin = null)
        {
            origin = origin ?? _defaultOrigin;
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

                var dbKey = GetDbKey(origin, name);
                var dbState = Databases.GetOrAdd(dbKey, _ =>
                {
                    var s = new DatabaseState(name) { Origin = origin };
                    // IDB §2.11: Load from persistent backend if available
                    if (_storageBackend != null)
                    {
                        LoadFromBackend(s, origin, name);
                    }
                    return s;
                });
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
                        // IDB §2.2.1: If another connection has an active versionchange, fire onblocked
                        if (_activeVersionchangeDb.ContainsKey(dbKey))
                        {
                            context.ScheduleCallback(() =>
                            {
                                var blockedEvt = new FenObject();
                                blockedEvt.Set("target", FenValue.FromObject(request));
                                blockedEvt.Set("oldVersion", FenValue.FromNumber(oldVersion));
                                blockedEvt.Set("newVersion", FenValue.FromNumber(version));
                                InvokeRequestHandler(request, "onblocked", blockedEvt, context);
                            }, 0);
                        }

                        dbState.Version = version;
                        dbState.Dirty = true;
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
                            _activeVersionchangeDb[dbKey] = true;
                            var upgradeEvent = new FenObject();
                            upgradeEvent.Set("target", FenValue.FromObject(request));
                            upgradeEvent.Set("oldVersion", FenValue.FromNumber(oldVersion));
                            upgradeEvent.Set("newVersion", FenValue.FromNumber(version));
                            request.Set("transaction", FenValue.FromObject(CreateTransaction(dbState, new[] { "*" }, "versionchange", context, true, db)));
                            InvokeRequestHandler(request, "onupgradeneeded", upgradeEvent, context);
                            _activeVersionchangeDb.TryRemove(dbKey, out _);
                        }

                        db.Set("__schemaMutable", FenValue.FromBoolean(false));
                        request.Set("readyState", FenValue.FromString("done"));
                        InvokeRequestHandler(request, "onsuccess", CreateEvent(request), context);
                    }
                    catch (Exception ex)
                    {
                        _activeVersionchangeDb.TryRemove(dbKey, out _);
                        DispatchRequestError(request, ex.Message, context, null, null);
                    }
                }, 0);
            });

            return FenValue.FromObject(request);
        }

        private static FenValue DeleteDatabase(string name, IExecutionContext context, string origin = null)
        {
            origin = origin ?? _defaultOrigin;
            var dbKey = GetDbKey(origin, name);
            var request = CreateRequest();
            _ = RunDetachedAsync(async () =>
            {
                await Task.Delay(10).ConfigureAwait(false);
                Databases.TryRemove(dbKey, out _);
                // IDB §2.11: Delete from persistent storage
                if (_storageBackend != null)
                {
                    try { await _storageBackend.DeleteDatabase(origin, name).ConfigureAwait(false); }
                    catch (Exception ex) { FenBrowser.Core.FenLogger.Warn($"[IndexedDB] Backend delete failed: {ex.Message}", LogCategory.Storage); }
                }
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
                    dbState.Dirty = true;
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

                    dbState.Dirty = true;
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
            // IDB §4.4: Track pending request count for auto-commit.
            // When all requests complete and no new ones are queued, the transaction auto-commits.
            var pendingRequests = 0;
            var autoCommitScheduled = false;
            var txnLock = new object();

            // Internal helper: check if transaction should auto-commit after request completion.
            void TryAutoCommit()
            {
                lock (txnLock)
                {
                    if (completed || aborted || pendingRequests > 0 || autoCommitScheduled) return;
                    autoCommitScheduled = true;
                }
                // Schedule auto-commit on next microtask checkpoint per IDB §4.4
                context.ScheduleCallback(() =>
                {
                    lock (txnLock)
                    {
                        if (completed || aborted) return;
                        // Re-check: more requests may have been queued during the microtask
                        if (pendingRequests > 0) { autoCommitScheduled = false; return; }
                        completed = true;
                    }
                    // IDB §2.11: Flush to persistent storage on commit
                    _ = FlushToBackend(dbState);
                    InvokeRequestHandler(transaction, "oncomplete", CreateEvent(transaction), context);
                }, 0);
            }

            // Internal helper: abort the transaction due to a request error per IDB §4.5
            void AbortFromError(string error)
            {
                lock (txnLock)
                {
                    if (completed || aborted) return;
                    aborted = true;
                }
                transaction.Set("error", FenValue.FromString(error ?? "AbortError"));
                context.ScheduleCallback(() =>
                {
                    InvokeRequestHandler(transaction, "onabort", CreateEvent(transaction), context);
                }, 0);
            }

            transaction.Set("db", FenValue.FromObject(db));
            transaction.Set("mode", FenValue.FromString(normalizedMode));
            transaction.Set("error", FenValue.Null);
            transaction.Set("oncomplete", FenValue.Null);
            transaction.Set("onerror", FenValue.Null);
            transaction.Set("onabort", FenValue.Null);
            transaction.Set("objectStoreNames", FenValue.FromObject(CreateStringArray(requestedStoreNames.Where(name => name != "*"))));

            // Store internal helpers as NativeObject so object store requests can use them
            transaction.NativeObject = new TransactionInternals
            {
                IncrementPending = () => { lock (txnLock) { pendingRequests++; autoCommitScheduled = false; } },
                DecrementPending = () => { lock (txnLock) { pendingRequests--; } TryAutoCommit(); },
                AbortFromError = AbortFromError,
                IsActive = () => { lock (txnLock) { return !completed && !aborted; } }
            };

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
                lock (txnLock)
                {
                    if (completed || aborted) return FenValue.Undefined;
                    completed = true;
                }
                context.ScheduleCallback(() =>
                {
                    // IDB §2.11: Flush to persistent storage on commit
                    _ = FlushToBackend(dbState);
                    InvokeRequestHandler(transaction, "oncomplete", CreateEvent(transaction), context);
                }, 0);
                return FenValue.Undefined;
            })));

            transaction.Set("abort", FenValue.FromFunction(new FenFunction("abort", (args, thisVal) =>
            {
                AbortFromError("AbortError");
                return FenValue.Undefined;
            })));

            return transaction;
        }

        /// <summary>Internal state for transaction request tracking and auto-commit.</summary>
        private sealed class TransactionInternals
        {
            public Action IncrementPending;
            public Action DecrementPending;
            public Action<string> AbortFromError;
            public Func<bool> IsActive;
        }

        /// <summary>Gets the transaction internals if available.</summary>
        private static TransactionInternals GetTxnInternals(FenObject transaction)
        {
            return transaction?.NativeObject as TransactionInternals;
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
                var request = CreateRequest(transaction);
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
                        dbState.Dirty = true;
                        DispatchRequestSuccess(request, FenValue.FromString(key), context, objectStore, transaction);
                    }
                });

                return FenValue.FromObject(request);
            })));

            objectStore.Set("put", FenValue.FromFunction(new FenFunction("put", (args, thisVal) =>
            {
                var request = CreateRequest(transaction);
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
                        dbState.Dirty = true;
                        DispatchRequestSuccess(request, FenValue.FromString(key), context, objectStore, transaction);
                    }
                });

                return FenValue.FromObject(request);
            })));

            objectStore.Set("get", FenValue.FromFunction(new FenFunction("get", (args, thisVal) =>
            {
                var request = CreateRequest(transaction);
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
                var request = CreateRequest(transaction);
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
                    dbState.Dirty = true;
                    DispatchRequestSuccess(request, FenValue.Undefined, context, objectStore, transaction);
                });

                return FenValue.FromObject(request);
            })));

            objectStore.Set("clear", FenValue.FromFunction(new FenFunction("clear", (args, thisVal) =>
            {
                var request = CreateRequest(transaction);
                if (readOnly)
                {
                    DispatchRequestError(request, "ReadOnlyError: Transaction is readonly.", context, objectStore, transaction);
                    return FenValue.FromObject(request);
                }

                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    storeState.Records.Clear();
                    dbState.Dirty = true;
                    DispatchRequestSuccess(request, FenValue.Undefined, context, objectStore, transaction);
                });

                return FenValue.FromObject(request);
            })));

            objectStore.Set("count", FenValue.FromFunction(new FenFunction("count", (args, thisVal) =>
            {
                var request = CreateRequest(transaction);
                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    DispatchRequestSuccess(request, FenValue.FromNumber(storeState.Records.Count), context, objectStore, transaction);
                });
                return FenValue.FromObject(request);
            })));

            objectStore.Set("getAll", FenValue.FromFunction(new FenFunction("getAll", (args, thisVal) =>
            {
                var request = CreateRequest(transaction);
                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    DispatchRequestSuccess(request, FenValue.FromObject(CreateValueArray(storeState.Records.Values.ToList())), context, objectStore, transaction);
                });
                return FenValue.FromObject(request);
            })));

            // IDB §2.9.4: getAllKeys — returns all keys in the store
            objectStore.Set("getAllKeys", FenValue.FromFunction(new FenFunction("getAllKeys", (args, thisVal) =>
            {
                var request = CreateRequest(transaction);
                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    var keys = storeState.Records.Keys.ToList();
                    var arr = new FenObject();
                    for (int i = 0; i < keys.Count; i++)
                        arr.Set(i.ToString(), FenValue.FromString(keys[i]));
                    arr.Set("length", FenValue.FromNumber(keys.Count));
                    DispatchRequestSuccess(request, FenValue.FromObject(arr), context, objectStore, transaction);
                });
                return FenValue.FromObject(request);
            })));

            // IDB §2.9.7: createIndex — creates an index on the object store (only during upgrade)
            objectStore.Set("createIndex", FenValue.FromFunction(new FenFunction("createIndex", (args, thisVal) =>
            {
                if (args.Length < 2) throw new FenBrowser.FenEngine.Errors.FenTypeError("TypeError: createIndex requires name and keyPath");
                string indexName = args[0].ToString();
                string indexKeyPath = args[1].ToString();
                bool unique = false, multiEntry = false;
                if (args.Length > 2 && args[2].IsObject)
                {
                    var opts = args[2].AsObject();
                    var uVal = opts.Get("unique");
                    if (!uVal.IsUndefined) unique = uVal.ToBoolean();
                    var mVal = opts.Get("multiEntry");
                    if (!mVal.IsUndefined) multiEntry = mVal.ToBoolean();
                }
                storeState.Indexes[indexName] = new IndexState(indexName, indexKeyPath, unique, multiEntry);
                // Return an IDBIndex-like object
                var idx = CreateIndexObject(storeState, indexName, context, objectStore, transaction);
                return FenValue.FromObject(idx);
            })));

            // IDB §2.9.8: deleteIndex
            objectStore.Set("deleteIndex", FenValue.FromFunction(new FenFunction("deleteIndex", (args, thisVal) =>
            {
                if (args.Length < 1) return FenValue.Undefined;
                storeState.Indexes.TryRemove(args[0].ToString(), out _);
                return FenValue.Undefined;
            })));

            // IDB §2.9.6: index — returns an IDBIndex for the named index
            objectStore.Set("index", FenValue.FromFunction(new FenFunction("index", (args, thisVal) =>
            {
                if (args.Length < 1) throw new FenBrowser.FenEngine.Errors.FenTypeError("TypeError: index requires a name");
                string indexName = args[0].ToString();
                if (!storeState.Indexes.ContainsKey(indexName))
                    throw new FenBrowser.FenEngine.Errors.FenTypeError($"NotFoundError: No index named '{indexName}'");
                return FenValue.FromObject(CreateIndexObject(storeState, indexName, context, objectStore, transaction));
            })));

            // IDB §2.9.5: openCursor(query, direction) — creates a cursor over the object store
            objectStore.Set("openCursor", FenValue.FromFunction(new FenFunction("openCursor", (args, thisVal) =>
            {
                var request = CreateRequest(transaction);
                var query = args.Length > 0 && !args[0].IsUndefined && !args[0].IsNull ? args[0] : FenValue.Undefined;
                var direction = args.Length > 1 && args[1].IsString ? args[1].ToString() : "next";
                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    var entries = FilterAndSortEntries(storeState.Records.ToList(), query, direction);
                    if (entries.Count == 0)
                    {
                        DispatchRequestSuccess(request, FenValue.Null, context, objectStore, transaction);
                        return;
                    }
                    int cursorIndex = 0;
                    var cursor = CreateCursorObject(entries, ref cursorIndex, request, context, objectStore, transaction, direction);
                    DispatchRequestSuccess(request, FenValue.FromObject(cursor), context, objectStore, transaction);
                });
                return FenValue.FromObject(request);
            })));

            // IDB §2.9.5: openKeyCursor — like openCursor but returns key-only cursor (no value property)
            objectStore.Set("openKeyCursor", FenValue.FromFunction(new FenFunction("openKeyCursor", (args, thisVal) =>
            {
                var request = CreateRequest(transaction);
                var query = args.Length > 0 && !args[0].IsUndefined && !args[0].IsNull ? args[0] : FenValue.Undefined;
                var direction = args.Length > 1 && args[1].IsString ? args[1].ToString() : "next";
                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    var entries = FilterAndSortEntries(storeState.Records.ToList(), query, direction);
                    if (entries.Count == 0)
                    {
                        DispatchRequestSuccess(request, FenValue.Null, context, objectStore, transaction);
                        return;
                    }
                    int cursorIndex = 0;
                    var cursor = CreateKeyCursorObject(entries, ref cursorIndex, request, context, objectStore, transaction, direction);
                    DispatchRequestSuccess(request, FenValue.FromObject(cursor), context, objectStore, transaction);
                });
                return FenValue.FromObject(request);
            })));

            // IDB §2.9: indexNames — DOMStringList of index names
            var indexNamesArr = new FenObject();
            int idxI = 0;
            foreach (var name in storeState.Indexes.Keys)
            {
                indexNamesArr.Set(idxI.ToString(), FenValue.FromString(name));
                idxI++;
            }
            indexNamesArr.Set("length", FenValue.FromNumber(idxI));
            objectStore.Set("indexNames", FenValue.FromObject(indexNamesArr));

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

        private static FenObject CreateRequest(FenObject transaction = null)
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
            // IDB §4.4: Track this request against the transaction for auto-commit
            GetTxnInternals(transaction)?.IncrementPending();
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
                // IDB §4.4: Decrement pending request count for auto-commit
                GetTxnInternals(transaction)?.DecrementPending();
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
                // IDB §4.5: Request error aborts the containing transaction
                var txn = GetTxnInternals(transaction);
                if (txn != null)
                {
                    txn.DecrementPending();
                    txn.AbortFromError(error ?? "UnknownError");
                }
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

        /// <summary>
        /// Creates an IDBIndex-like object for accessing records by a secondary key path.
        /// IDB §2.5: Each index has a key path, unique flag, and multiEntry flag.
        /// </summary>
        private static FenObject CreateIndexObject(ObjectStoreState storeState, string indexName,
            IExecutionContext context, FenObject objectStore, FenObject transaction)
        {
            if (!storeState.Indexes.TryGetValue(indexName, out var idxState))
                throw new Errors.FenTypeError($"NotFoundError: No index '{indexName}'");

            var idx = new FenObject();
            idx.Set("name", FenValue.FromString(idxState.Name));
            idx.Set("keyPath", FenValue.FromString(idxState.KeyPath));
            idx.Set("unique", FenValue.FromBoolean(idxState.Unique));
            idx.Set("multiEntry", FenValue.FromBoolean(idxState.MultiEntry));
            idx.Set("objectStore", FenValue.FromObject(objectStore));

            // IDBIndex.get(key) — find first record whose index key matches
            idx.Set("get", FenValue.FromFunction(new FenFunction("get", (args, thisVal) =>
            {
                var request = CreateRequest(transaction);
                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    string searchKey = args.Length > 0 ? args[0].ToString() : "";
                    FenValue result = FenValue.Undefined;
                    foreach (var kvp in storeState.Records)
                    {
                        if (kvp.Value.IsObject)
                        {
                            var fieldVal = kvp.Value.AsObject().Get(idxState.KeyPath);
                            if (!fieldVal.IsUndefined && fieldVal.ToString() == searchKey)
                            {
                                result = kvp.Value;
                                break;
                            }
                        }
                    }
                    DispatchRequestSuccess(request, result, context, objectStore, transaction);
                });
                return FenValue.FromObject(request);
            })));

            // IDBIndex.getAll(query) — returns all matching records
            idx.Set("getAll", FenValue.FromFunction(new FenFunction("getAll", (args, thisVal) =>
            {
                var request = CreateRequest(transaction);
                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    string searchKey = args.Length > 0 && !args[0].IsUndefined ? args[0].ToString() : null;
                    var results = new List<FenValue>();
                    foreach (var kvp in storeState.Records)
                    {
                        if (searchKey == null)
                        {
                            results.Add(kvp.Value);
                        }
                        else if (kvp.Value.IsObject)
                        {
                            var fieldVal = kvp.Value.AsObject().Get(idxState.KeyPath);
                            if (!fieldVal.IsUndefined && fieldVal.ToString() == searchKey)
                                results.Add(kvp.Value);
                        }
                    }
                    DispatchRequestSuccess(request, FenValue.FromObject(CreateValueArray(results)), context, objectStore, transaction);
                });
                return FenValue.FromObject(request);
            })));

            // IDBIndex.count(key)
            idx.Set("count", FenValue.FromFunction(new FenFunction("count", (args, thisVal) =>
            {
                var request = CreateRequest(transaction);
                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    string searchKey = args.Length > 0 && !args[0].IsUndefined ? args[0].ToString() : null;
                    int count = 0;
                    foreach (var kvp in storeState.Records)
                    {
                        if (searchKey == null) { count++; continue; }
                        if (kvp.Value.IsObject)
                        {
                            var fieldVal = kvp.Value.AsObject().Get(idxState.KeyPath);
                            if (!fieldVal.IsUndefined && fieldVal.ToString() == searchKey) count++;
                        }
                    }
                    DispatchRequestSuccess(request, FenValue.FromNumber(count), context, objectStore, transaction);
                });
                return FenValue.FromObject(request);
            })));

            // IDBIndex.getKey(key) — returns the primary key of the first match
            idx.Set("getKey", FenValue.FromFunction(new FenFunction("getKey", (args, thisVal) =>
            {
                var request = CreateRequest(transaction);
                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    string searchKey = args.Length > 0 ? args[0].ToString() : "";
                    FenValue result = FenValue.Undefined;
                    foreach (var kvp in storeState.Records)
                    {
                        if (kvp.Value.IsObject)
                        {
                            var fieldVal = kvp.Value.AsObject().Get(idxState.KeyPath);
                            if (!fieldVal.IsUndefined && fieldVal.ToString() == searchKey)
                            {
                                result = FenValue.FromString(kvp.Key);
                                break;
                            }
                        }
                    }
                    DispatchRequestSuccess(request, result, context, objectStore, transaction);
                });
                return FenValue.FromObject(request);
            })));

            // IDBIndex.openCursor([query], [direction]) — iterate over index entries with values
            idx.Set("openCursor", FenValue.FromFunction(new FenFunction("openCursor", (args, thisVal) =>
            {
                var request = CreateRequest(transaction);
                var query = args.Length > 0 && !args[0].IsUndefined && !args[0].IsNull ? args[0] : FenValue.Undefined;
                var direction = args.Length > 1 && args[1].IsString ? args[1].ToString() : "next";
                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    // Build entries from the index's keyPath perspective
                    var indexEntries = new List<KeyValuePair<string, FenValue>>();
                    foreach (var kvp in storeState.Records)
                    {
                        if (kvp.Value.IsObject)
                        {
                            var indexKey = kvp.Value.AsObject().Get(idxState.KeyPath);
                            if (!indexKey.IsUndefined)
                                indexEntries.Add(new KeyValuePair<string, FenValue>(indexKey.ToString(), kvp.Value));
                        }
                    }
                    var entries = FilterAndSortEntries(indexEntries, query, direction);
                    if (entries.Count == 0)
                    {
                        DispatchRequestSuccess(request, FenValue.Null, context, objectStore, transaction);
                        return;
                    }
                    int cursorIndex = 0;
                    var cursor = CreateCursorObject(entries, ref cursorIndex, request, context, objectStore, transaction, direction);
                    DispatchRequestSuccess(request, FenValue.FromObject(cursor), context, objectStore, transaction);
                });
                return FenValue.FromObject(request);
            })));

            // IDBIndex.openKeyCursor([query], [direction]) — iterate over index keys only
            idx.Set("openKeyCursor", FenValue.FromFunction(new FenFunction("openKeyCursor", (args, thisVal) =>
            {
                var request = CreateRequest(transaction);
                var query = args.Length > 0 && !args[0].IsUndefined && !args[0].IsNull ? args[0] : FenValue.Undefined;
                var direction = args.Length > 1 && args[1].IsString ? args[1].ToString() : "next";
                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    var indexEntries = new List<KeyValuePair<string, FenValue>>();
                    foreach (var kvp in storeState.Records)
                    {
                        if (kvp.Value.IsObject)
                        {
                            var indexKey = kvp.Value.AsObject().Get(idxState.KeyPath);
                            if (!indexKey.IsUndefined)
                                indexEntries.Add(new KeyValuePair<string, FenValue>(indexKey.ToString(), kvp.Value));
                        }
                    }
                    var entries = FilterAndSortEntries(indexEntries, query, direction);
                    if (entries.Count == 0)
                    {
                        DispatchRequestSuccess(request, FenValue.Null, context, objectStore, transaction);
                        return;
                    }
                    int cursorIndex = 0;
                    var cursor = CreateKeyCursorObject(entries, ref cursorIndex, request, context, objectStore, transaction, direction);
                    DispatchRequestSuccess(request, FenValue.FromObject(cursor), context, objectStore, transaction);
                });
                return FenValue.FromObject(request);
            })));

            return idx;
        }

        /// <summary>
        /// Creates an IDBCursor-like object for iterating over records.
        /// IDB §2.6: Cursor has key, primaryKey, value, direction, and continue/advance methods.
        /// </summary>
        private static FenObject CreateCursorObject(List<KeyValuePair<string, FenValue>> entries,
            ref int cursorIndex, FenObject request, IExecutionContext context,
            FenObject objectStore, FenObject transaction, string direction = "next")
        {
            int idx = cursorIndex;
            var cursor = new FenObject();
            cursor.Set("key", FenValue.FromString(entries[idx].Key));
            cursor.Set("primaryKey", FenValue.FromString(entries[idx].Key));
            cursor.Set("value", entries[idx].Value);
            cursor.Set("direction", FenValue.FromString(direction));

            // IDBCursor.update(value) — update the current record
            cursor.Set("update", FenValue.FromFunction(new FenFunction("update", (args, thisVal) =>
            {
                var updateRequest = CreateRequest(transaction);
                if (idx >= entries.Count)
                {
                    DispatchRequestError(updateRequest, "InvalidStateError: Cursor is exhausted.", context, objectStore, transaction);
                    return FenValue.FromObject(updateRequest);
                }
                var newValue = args.Length > 0 ? args[0] : FenValue.Undefined;
                var key = entries[idx].Key;
                // Defer to DispatchRequestSuccess for scheduling
                context.ScheduleCallback(() =>
                {
                    // Update the underlying store directly
                    entries[idx] = new KeyValuePair<string, FenValue>(key, newValue);
                    DispatchRequestSuccess(updateRequest, FenValue.FromString(key), context, objectStore, transaction);
                }, 0);
                return FenValue.FromObject(updateRequest);
            })));

            // IDBCursor.delete() — delete the current record
            cursor.Set("delete", FenValue.FromFunction(new FenFunction("delete", (args, thisVal) =>
            {
                var deleteRequest = CreateRequest(transaction);
                if (idx >= entries.Count)
                {
                    DispatchRequestError(deleteRequest, "InvalidStateError: Cursor is exhausted.", context, objectStore, transaction);
                    return FenValue.FromObject(deleteRequest);
                }
                context.ScheduleCallback(() =>
                {
                    DispatchRequestSuccess(deleteRequest, FenValue.Undefined, context, objectStore, transaction);
                }, 0);
                return FenValue.FromObject(deleteRequest);
            })));

            // IDBCursor.continue() — advance to next entry and re-fire onsuccess
            cursor.Set("continue", FenValue.FromFunction(new FenFunction("continue", (args, thisVal) =>
            {
                idx++;
                if (idx < entries.Count)
                {
                    var innerCursor = CreateCursorObject(entries, ref idx, request, context, objectStore, transaction, direction);
                    DispatchRequestSuccess(request, FenValue.FromObject(innerCursor), context, objectStore, transaction);
                }
                else
                {
                    DispatchRequestSuccess(request, FenValue.Null, context, objectStore, transaction);
                }
                return FenValue.Undefined;
            })));

            // IDBCursor.advance(count)
            cursor.Set("advance", FenValue.FromFunction(new FenFunction("advance", (args, thisVal) =>
            {
                int count = args.Length > 0 ? (int)args[0].ToNumber() : 1;
                idx += count;
                if (idx < entries.Count)
                {
                    var nextCursor = CreateCursorObject(entries, ref idx, request, context, objectStore, transaction, direction);
                    DispatchRequestSuccess(request, FenValue.FromObject(nextCursor), context, objectStore, transaction);
                }
                else
                {
                    DispatchRequestSuccess(request, FenValue.Null, context, objectStore, transaction);
                }
                return FenValue.Undefined;
            })));

            return cursor;
        }

        /// <summary>
        /// Creates a key-only cursor (IDBCursor without value/update/delete).
        /// Used by openKeyCursor() per IDB §2.9.5.
        /// </summary>
        private static FenObject CreateKeyCursorObject(List<KeyValuePair<string, FenValue>> entries,
            ref int cursorIndex, FenObject request, IExecutionContext context,
            FenObject objectStore, FenObject transaction, string direction = "next")
        {
            int idx = cursorIndex;
            var cursor = new FenObject();
            cursor.Set("key", FenValue.FromString(entries[idx].Key));
            cursor.Set("primaryKey", FenValue.FromString(entries[idx].Key));
            cursor.Set("direction", FenValue.FromString(direction));

            cursor.Set("continue", FenValue.FromFunction(new FenFunction("continue", (args, thisVal) =>
            {
                idx++;
                if (idx < entries.Count)
                {
                    var innerCursor = CreateKeyCursorObject(entries, ref idx, request, context, objectStore, transaction, direction);
                    DispatchRequestSuccess(request, FenValue.FromObject(innerCursor), context, objectStore, transaction);
                }
                else
                {
                    DispatchRequestSuccess(request, FenValue.Null, context, objectStore, transaction);
                }
                return FenValue.Undefined;
            })));

            cursor.Set("advance", FenValue.FromFunction(new FenFunction("advance", (args, thisVal) =>
            {
                int count = args.Length > 0 ? (int)args[0].ToNumber() : 1;
                idx += count;
                if (idx < entries.Count)
                {
                    var nextCursor = CreateKeyCursorObject(entries, ref idx, request, context, objectStore, transaction, direction);
                    DispatchRequestSuccess(request, FenValue.FromObject(nextCursor), context, objectStore, transaction);
                }
                else
                {
                    DispatchRequestSuccess(request, FenValue.Null, context, objectStore, transaction);
                }
                return FenValue.Undefined;
            })));

            return cursor;
        }

        /// <summary>
        /// Filters entries by an IDBKeyRange query and sorts by direction.
        /// IDB §2.6.5: Cursor direction determines iteration order.
        /// </summary>
        private static List<KeyValuePair<string, FenValue>> FilterAndSortEntries(
            List<KeyValuePair<string, FenValue>> entries, FenValue query, string direction)
        {
            // Apply IDBKeyRange filter if query is an object with lower/upper properties
            if (!query.IsUndefined && query.IsObject)
            {
                var range = query.AsObject();
                var lower = range.Get("lower");
                var upper = range.Get("upper");
                var lowerOpen = range.Get("lowerOpen").ToBoolean();
                var upperOpen = range.Get("upperOpen").ToBoolean();

                entries = entries.Where(e =>
                {
                    var key = e.Key;
                    if (!lower.IsUndefined)
                    {
                        int cmp = string.Compare(key, lower.ToString(), StringComparison.Ordinal);
                        if (lowerOpen ? cmp <= 0 : cmp < 0) return false;
                    }
                    if (!upper.IsUndefined)
                    {
                        int cmp = string.Compare(key, upper.ToString(), StringComparison.Ordinal);
                        if (upperOpen ? cmp >= 0 : cmp > 0) return false;
                    }
                    return true;
                }).ToList();
            }
            else if (!query.IsUndefined && query.IsString)
            {
                // Single key query
                var searchKey = query.ToString();
                entries = entries.Where(e => e.Key == searchKey).ToList();
            }

            // Sort by direction
            if (string.Equals(direction, "prev", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(direction, "prevunique", StringComparison.OrdinalIgnoreCase))
            {
                entries.Reverse();
            }

            return entries;
        }

        /// <summary>
        /// Registers the IDBKeyRange global constructor.
        /// IDB §2.4: IDBKeyRange has only, lowerBound, upperBound, bound static methods.
        /// </summary>
        public static void RegisterIDBKeyRange(FenObject global)
        {
            var keyRange = new FenObject();

            // IDBKeyRange.only(value) — range containing a single key
            keyRange.Set("only", FenValue.FromFunction(new FenFunction("only", (args, thisVal) =>
            {
                var range = new FenObject();
                var val = args.Length > 0 ? args[0] : FenValue.Undefined;
                range.Set("lower", val);
                range.Set("upper", val);
                range.Set("lowerOpen", FenValue.FromBoolean(false));
                range.Set("upperOpen", FenValue.FromBoolean(false));
                range.Set("includes", FenValue.FromFunction(new FenFunction("includes", (a, t) =>
                    FenValue.FromBoolean(a.Length > 0 && a[0].ToString() == val.ToString()))));
                return FenValue.FromObject(range);
            })));

            // IDBKeyRange.lowerBound(lower, open)
            keyRange.Set("lowerBound", FenValue.FromFunction(new FenFunction("lowerBound", (args, thisVal) =>
            {
                var range = new FenObject();
                var lower = args.Length > 0 ? args[0] : FenValue.Undefined;
                var lowerOpen = args.Length > 1 && args[1].ToBoolean();
                range.Set("lower", lower);
                range.Set("upper", FenValue.Undefined);
                range.Set("lowerOpen", FenValue.FromBoolean(lowerOpen));
                range.Set("upperOpen", FenValue.FromBoolean(true));
                AddKeyRangeIncludes(range, lower, FenValue.Undefined, lowerOpen, true);
                return FenValue.FromObject(range);
            })));

            // IDBKeyRange.upperBound(upper, open)
            keyRange.Set("upperBound", FenValue.FromFunction(new FenFunction("upperBound", (args, thisVal) =>
            {
                var range = new FenObject();
                var upper = args.Length > 0 ? args[0] : FenValue.Undefined;
                var upperOpen = args.Length > 1 && args[1].ToBoolean();
                range.Set("lower", FenValue.Undefined);
                range.Set("upper", upper);
                range.Set("lowerOpen", FenValue.FromBoolean(true));
                range.Set("upperOpen", FenValue.FromBoolean(upperOpen));
                AddKeyRangeIncludes(range, FenValue.Undefined, upper, true, upperOpen);
                return FenValue.FromObject(range);
            })));

            // IDBKeyRange.bound(lower, upper, lowerOpen, upperOpen)
            keyRange.Set("bound", FenValue.FromFunction(new FenFunction("bound", (args, thisVal) =>
            {
                var range = new FenObject();
                var lower = args.Length > 0 ? args[0] : FenValue.Undefined;
                var upper = args.Length > 1 ? args[1] : FenValue.Undefined;
                var lowerOpen = args.Length > 2 && args[2].ToBoolean();
                var upperOpen = args.Length > 3 && args[3].ToBoolean();
                range.Set("lower", lower);
                range.Set("upper", upper);
                range.Set("lowerOpen", FenValue.FromBoolean(lowerOpen));
                range.Set("upperOpen", FenValue.FromBoolean(upperOpen));
                AddKeyRangeIncludes(range, lower, upper, lowerOpen, upperOpen);
                return FenValue.FromObject(range);
            })));

            global.Set("IDBKeyRange", FenValue.FromObject(keyRange));
        }

        /// <summary>
        /// IDB §2.4: Adds an includes(key) method to an IDBKeyRange object.
        /// </summary>
        private static void AddKeyRangeIncludes(FenObject range, FenValue lower, FenValue upper, bool lowerOpen, bool upperOpen)
        {
            range.Set("includes", FenValue.FromFunction(new FenFunction("includes", (a, t) =>
            {
                if (a.Length == 0) return FenValue.FromBoolean(false);
                var key = a[0].ToString();
                var hasLower = !lower.IsUndefined && !lower.IsNull;
                var hasUpper = !upper.IsUndefined && !upper.IsNull;
                if (hasLower)
                {
                    var cmp = string.Compare(key, lower.ToString(), StringComparison.Ordinal);
                    if (lowerOpen ? cmp <= 0 : cmp < 0) return FenValue.FromBoolean(false);
                }
                if (hasUpper)
                {
                    var cmp = string.Compare(key, upper.ToString(), StringComparison.Ordinal);
                    if (upperOpen ? cmp >= 0 : cmp > 0) return FenValue.FromBoolean(false);
                }
                return FenValue.FromBoolean(true);
            })));
        }

        #region Storage Backend Persistence (IDB §2.11)

        /// <summary>
        /// Load database state from persistent backend into in-memory cache.
        /// Called once when a database is first opened in this session.
        /// </summary>
        private static void LoadFromBackend(DatabaseState dbState, string origin, string dbName)
        {
            if (_storageBackend == null) return;
            try
            {
                var info = _storageBackend.GetDatabaseInfo(origin, dbName).GetAwaiter().GetResult();
                if (info == null) return;

                dbState.Version = info.Version;
                foreach (var storeName in info.ObjectStoreNames)
                {
                    var storeState = new ObjectStoreState(storeName, null, false);
                    // Load records from backend
                    var records = _storageBackend.GetAll(origin, dbName, storeName).GetAwaiter().GetResult();
                    var keys = _storageBackend.GetAllKeys(origin, dbName, storeName).GetAwaiter().GetResult();
                    var keyList = keys?.ToList() ?? new List<object>();
                    var recordList = records?.ToList() ?? new List<object>();
                    for (int i = 0; i < Math.Min(keyList.Count, recordList.Count); i++)
                    {
                        var key = keyList[i]?.ToString() ?? i.ToString();
                        storeState.Records[key] = StorageUtils.FromSerializable(recordList[i]);
                    }
                    dbState.Stores[storeName] = storeState;
                }
                FenBrowser.Core.FenLogger.Debug($"[IndexedDB] Loaded '{dbName}' from backend ({dbState.Stores.Count} stores)", LogCategory.Storage);
            }
            catch (Exception ex)
            {
                FenBrowser.Core.FenLogger.Warn($"[IndexedDB] Backend load failed for '{dbName}': {ex.Message}", LogCategory.Storage);
            }
        }

        /// <summary>
        /// Flush dirty database state to persistent backend.
        /// Called on transaction commit to ensure durability per IDB §4.4.
        /// </summary>
        private static async Task FlushToBackend(DatabaseState dbState)
        {
            if (_storageBackend == null || !dbState.Dirty) return;
            var origin = dbState.Origin;
            var dbName = dbState.Name;
            try
            {
                // Ensure database exists in backend
                await _storageBackend.OpenDatabase(origin, dbName, dbState.Version).ConfigureAwait(false);

                // Sync each object store
                foreach (var kvp in dbState.Stores)
                {
                    var storeName = kvp.Key;
                    var storeState = kvp.Value;

                    // Ensure object store exists
                    try
                    {
                        await _storageBackend.CreateObjectStore(origin, dbName, storeName,
                            new ObjectStoreOptions { KeyPath = storeState.KeyPath, AutoIncrement = storeState.AutoIncrement })
                            .ConfigureAwait(false);
                    }
                    catch { /* Store may already exist */ }

                    // Clear and re-write all records (simple but correct)
                    await _storageBackend.Clear(origin, dbName, storeName).ConfigureAwait(false);
                    foreach (var record in storeState.Records)
                    {
                        var serializable = StorageUtils.ToSerializable(record.Value);
                        await _storageBackend.Put(origin, dbName, storeName, record.Key, serializable).ConfigureAwait(false);
                    }
                }

                dbState.Dirty = false;
                FenBrowser.Core.FenLogger.Debug($"[IndexedDB] Flushed '{dbName}' to backend", LogCategory.Storage);
            }
            catch (Exception ex)
            {
                FenBrowser.Core.FenLogger.Warn($"[IndexedDB] Backend flush failed for '{dbName}': {ex.Message}", LogCategory.Storage);
            }
        }

        #endregion
    }
}
