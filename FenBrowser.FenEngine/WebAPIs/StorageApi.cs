using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Errors;

namespace FenBrowser.FenEngine.WebAPIs
{
    public static class StorageApi
    {
        // Thread-safe dictionary: Origin -> (Key -> Value)
        private static ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _localStorage = new();
        private static ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _sessionStorage = new();
        private static readonly object _ioLock = new object();
        private static readonly object _saveTimerLock = new object();
        private static Timer _saveTimer;
        private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromMilliseconds(150);
        
        // Configurable path for persistence
        public static string LocalStoragePath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FenBrowser_Data", "localStorage.json");

        // Static constructor to load data once
        static StorageApi()
        {
            Load();
        }

        public static void ResetForTesting()
        {
            lock (_saveTimerLock)
            {
                _saveTimer?.Dispose();
                _saveTimer = null;
            }

            _localStorage.Clear();
            _sessionStorage.Clear();
            if (File.Exists(LocalStoragePath))
            {
                // Force reload
                Load();
            }
        }

        private static void Load()
        {
            lock (_ioLock)
            {
                try
                {
                    if (File.Exists(LocalStoragePath))
                    {
                        var json = File.ReadAllText(LocalStoragePath);
                        var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json);
                        if (data != null)
                        {
                            foreach (var origin in data)
                            {
                                var dict = new ConcurrentDictionary<string, string>(origin.Value);
                                _localStorage[origin.Key] = dict;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log error but don't crash
                    Console.WriteLine($"[StorageApi] Load failed: {ex.Message}");
                }
            }
        }

        public static void Save()
        {
            lock (_saveTimerLock)
            {
                _saveTimer?.Dispose();
                _saveTimer = null;
            }

            SaveNow();
        }

        private static void ScheduleSave()
        {
            lock (_saveTimerLock)
            {
                _saveTimer?.Dispose();
                _saveTimer = new Timer(_ =>
                {
                    lock (_saveTimerLock)
                    {
                        _saveTimer?.Dispose();
                        _saveTimer = null;
                    }

                    SaveNow();
                }, null, SaveDebounceDelay, Timeout.InfiniteTimeSpan);
            }
        }

        private static void SaveNow()
        {
            lock (_ioLock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(LocalStoragePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) 
                        Directory.CreateDirectory(dir);

                    // Snapshot the concurrent dictionary
                    var snapshot = new Dictionary<string, Dictionary<string, string>>();
                    foreach (var kvp in _localStorage)
                    {
                        snapshot[kvp.Key] = new Dictionary<string, string>(kvp.Value);
                    }

                    var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                    var tempPath = LocalStoragePath + ".tmp";
                    File.WriteAllText(tempPath, json);

                    if (File.Exists(LocalStoragePath))
                    {
                        File.Replace(tempPath, LocalStoragePath, null);
                    }
                    else
                    {
                        File.Move(tempPath, LocalStoragePath);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[StorageApi] Save failed: {ex.Message}");
                }
            }
        }

        public static void ClearSessionStorage()
        {
            _sessionStorage.Clear();
        }

        public static void ClearLocalStorage(bool deletePersistentFile = true)
        {
            _localStorage.Clear();

            if (!deletePersistentFile)
            {
                Save();
                return;
            }

            lock (_ioLock)
            {
                try
                {
                    if (File.Exists(LocalStoragePath))
                    {
                        File.Delete(LocalStoragePath);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[StorageApi] Clear local storage file failed: {ex.Message}");
                }
            }
        }

        public static void ClearAllStorage(bool deletePersistentFile = true)
        {
            ClearSessionStorage();
            ClearLocalStorage(deletePersistentFile);
        }

        public static string BuildSessionScope(string partitionId, string origin)
        {
            var normalizedPartition = string.IsNullOrWhiteSpace(partitionId) ? "default" : partitionId.Trim().ToLowerInvariant();
            return $"{normalizedPartition}:{NormalizeOrigin(origin)}";
        }

        public static string GetLocalStorageItem(string origin, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var store = _localStorage.GetOrAdd(NormalizeOrigin(origin), _ => new ConcurrentDictionary<string, string>());
            return store.TryGetValue(key, out var value) ? value : null;
        }

        public static void SetLocalStorageItem(string origin, string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            var store = _localStorage.GetOrAdd(NormalizeOrigin(origin), _ => new ConcurrentDictionary<string, string>());
            store[key] = value ?? string.Empty;
            ScheduleSave();
        }

        public static void RemoveLocalStorageItem(string origin, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            var store = _localStorage.GetOrAdd(NormalizeOrigin(origin), _ => new ConcurrentDictionary<string, string>());
            store.TryRemove(key, out _);
            ScheduleSave();
        }

        public static void ClearLocalStorage(string origin)
        {
            _localStorage.TryRemove(NormalizeOrigin(origin), out _);
            ScheduleSave();
        }

        public static IReadOnlyDictionary<string, string> GetAllLocalStorageItems(string origin)
        {
            var store = _localStorage.GetOrAdd(NormalizeOrigin(origin), _ => new ConcurrentDictionary<string, string>());
            return new Dictionary<string, string>(store);
        }

        public static string GetSessionStorageItem(string sessionScope, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var store = _sessionStorage.GetOrAdd(sessionScope ?? "session:null", _ => new ConcurrentDictionary<string, string>());
            return store.TryGetValue(key, out var value) ? value : null;
        }

        public static void SetSessionStorageItem(string sessionScope, string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            var store = _sessionStorage.GetOrAdd(sessionScope ?? "session:null", _ => new ConcurrentDictionary<string, string>());
            store[key] = value ?? string.Empty;
        }

        public static void RemoveSessionStorageItem(string sessionScope, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            var store = _sessionStorage.GetOrAdd(sessionScope ?? "session:null", _ => new ConcurrentDictionary<string, string>());
            store.TryRemove(key, out _);
        }

        public static void ClearSessionStorage(string sessionScope)
        {
            _sessionStorage.TryRemove(sessionScope ?? "session:null", out _);
        }

        public static IReadOnlyDictionary<string, string> GetAllSessionStorageItems(string sessionScope)
        {
            var store = _sessionStorage.GetOrAdd(sessionScope ?? "session:null", _ => new ConcurrentDictionary<string, string>());
            return new Dictionary<string, string>(store);
        }

        /// <summary>
        /// Creates a persistent, origin-keyed storage (localStorage).
        /// </summary>
        public static FenObject CreateLocalStorage(Func<string> getOrigin)
        {
             return new DomStorage("localStorage", () => NormalizeOrigin(getOrigin?.Invoke()), (origin) => 
             {
                 return _localStorage.GetOrAdd(origin, _ => new ConcurrentDictionary<string, string>());
             }, Save);
        }

        /// <summary>
        /// Creates a transient, tab-scoped storage (sessionStorage).
        /// When a stable partition id is supplied, storage persists across reloads within the
        /// same tab/session while remaining isolated from other tabs.
        /// </summary>
        public static FenObject CreateSessionStorage(Func<string> getOrigin = null, Func<string> getPartitionId = null)
        {
            // Fall back to per-instance isolation when no stable tab/session id is provided.
            var fallbackPartitionId = Guid.NewGuid().ToString("N");
            var originProvider = getOrigin ?? (() => "session");
            return new DomStorage("sessionStorage",
                () => BuildSessionScope(getPartitionId?.Invoke() ?? fallbackPartitionId, originProvider()),
                (originKey) => _sessionStorage.GetOrAdd(originKey, _ => new ConcurrentDictionary<string, string>()),
                null);
        }

        private static string NormalizeOrigin(string origin)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                return "null";
            }

            return origin.Trim().ToLowerInvariant();
        }

        /// <summary>Per-origin storage quota: 5 MB, measured as UTF-16 code units × 2 bytes (matching browser standard).</summary>
        public const long QuotaBytes = 5L * 1024 * 1024;

        /// <summary>
        /// Calculates total bytes used by a storage partition (all key+value pairs, UTF-16).
        /// </summary>
        private static long CalculateStoreBytes(ConcurrentDictionary<string, string> store)
            => store.Sum(kvp => ((long)kvp.Key.Length + kvp.Value.Length) * 2);

        public static long GetLocalStorageUsageBytes(string origin)
        {
            var store = _localStorage.GetOrAdd(NormalizeOrigin(origin), _ => new ConcurrentDictionary<string, string>());
            return CalculateStoreBytes(store);
        }

        public static long GetSessionStorageUsageBytes(string sessionScope)
        {
            var scope = sessionScope ?? "session:null";
            var store = _sessionStorage.GetOrAdd(scope, _ => new ConcurrentDictionary<string, string>());
            return CalculateStoreBytes(store);
        }

        public class DomStorage : FenObject
        {
            private readonly string _type;
            private readonly Func<string> _getOrigin;
            private readonly Func<string, ConcurrentDictionary<string, string>> _getStore;
            private readonly Action _onMutate;

            public DomStorage(string type, Func<string> getOrigin, Func<string, ConcurrentDictionary<string, string>> getStore, Action onMutate)
            {
                _type = type;
                _getOrigin = getOrigin;
                _getStore = getStore;
                _onMutate = onMutate;

                Set("getItem", FenValue.FromFunction(new FenFunction("getItem", GetItem)));
                Set("setItem", FenValue.FromFunction(new FenFunction("setItem", SetItem)));
                Set("removeItem", FenValue.FromFunction(new FenFunction("removeItem", RemoveItem)));
                Set("clear", FenValue.FromFunction(new FenFunction("clear", Clear)));
                Set("key", FenValue.FromFunction(new FenFunction("key", Key)));
                
                UpdateLength();
            }

            private ConcurrentDictionary<string, string> GetStore()
            {
                var origin = _getOrigin() ?? "null";
                return _getStore(origin);
            }

            private void UpdateLength()
            {
                var store = GetStore();
                Set("length", FenValue.FromNumber(store.Count));
            }

            private FenValue GetItem(FenValue[] args, FenValue thisVal)
            {
                if (args.Length == 0) return FenValue.Null;
                var key = args[0].ToString();
                var store = GetStore();
                return store.TryGetValue(key, out var val) ? FenValue.FromString(val) : FenValue.Null;
            }

            private FenValue SetItem(FenValue[] args, FenValue thisVal)
            {
                if (args.Length < 2) return FenValue.Undefined;
                var key = args[0].ToString();
                var val = args[1].ToString();

                var store = GetStore();

                // Per-origin quota check (5 MB, matching browser standard).
                // Compute bytes already used, subtract existing entry for this key (if any), add new entry.
                long usedBytes = CalculateStoreBytes(store);
                if (store.TryGetValue(key, out var existing))
                    usedBytes -= ((long)key.Length + existing.Length) * 2; // replacing — remove old size
                long newBytes = usedBytes + ((long)key.Length + val.Length) * 2;
                if (newBytes > QuotaBytes)
                    throw new FenResourceError($"QuotaExceededError: The {_type} quota of 5\u00a0MB per origin has been exceeded.");

                store[key] = val;
                UpdateLength();
                _onMutate?.Invoke();
                return FenValue.Undefined;
            }

            private FenValue RemoveItem(FenValue[] args, FenValue thisVal)
            {
                if (args.Length == 0) return FenValue.Undefined;
                var key = args[0].ToString();
                var store = GetStore();
                if (store.TryRemove(key, out _))
                {
                    UpdateLength();
                    _onMutate?.Invoke();
                }
                return FenValue.Undefined;
            }

            private FenValue Clear(FenValue[] args, FenValue thisVal)
            {
                var store = GetStore();
                store.Clear();
                UpdateLength();
                _onMutate?.Invoke();
                return FenValue.Undefined;
            }

            private FenValue Key(FenValue[] args, FenValue thisVal)
            {
                if (args.Length == 0) return FenValue.Null;
                var index = (int)args[0].ToNumber();
                var store = GetStore();
                var keys = store.Keys.ToList();
                if (index >= 0 && index < keys.Count)
                    return FenValue.FromString(keys[index]);
                return FenValue.Null;
            }
        }
    }
}

