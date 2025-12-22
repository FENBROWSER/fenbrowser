using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.WebAPIs
{
    public static class StorageApi
    {
        // Thread-safe dictionary: Origin -> (Key -> Value)
        private static ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _localStorage = new();
        private static ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _sessionStorage = new();
        private static readonly object _ioLock = new object();
        
        // Configurable path for persistence
        public static string LocalStoragePath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FenBrowser_Data", "localStorage.json");

        // Static constructor to load data once
        static StorageApi()
        {
            Load();
        }

        public static void ResetForTesting()
        {
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
            // Simple synchronous save for now. In production, this should be debounced/async.
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
                    File.WriteAllText(LocalStoragePath, json);
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

        /// <summary>
        /// Creates a persistent, origin-keyed storage (localStorage).
        /// </summary>
        public static FenObject CreateLocalStorage(Func<string> getOrigin)
        {
             return new DomStorage("localStorage", getOrigin, (origin) => 
             {
                 return _localStorage.GetOrAdd(origin, _ => new ConcurrentDictionary<string, string>());
             }, Save);
        }

        /// <summary>
        /// Creates a transient, in-memory storage (sessionStorage).
        /// Note: This simple implementation isolates per instance, which is correct for tabs.
        /// (Reload persistence requires tying this to a session ID, which we skip for now).
        /// </summary>
        public static FenObject CreateSessionStorage()
        {
            var store = new ConcurrentDictionary<string, string>();
            // Origin is ignored for the backing store lookup, but we might still use it for events later.
            return new DomStorage("sessionStorage", () => "session", (origin) => store, null);
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

            private IValue GetItem(IValue[] args, IValue thisVal)
            {
                if (args.Length == 0) return FenValue.Null;
                var key = args[0].ToString();
                var store = GetStore();
                return store.TryGetValue(key, out var val) ? FenValue.FromString(val) : FenValue.Null;
            }

            private IValue SetItem(IValue[] args, IValue thisVal)
            {
                if (args.Length < 2) return FenValue.Undefined;
                var key = args[0].ToString();
                var val = args[1].ToString();
                
                var store = GetStore();
                store[key] = val;
                UpdateLength();

                _onMutate?.Invoke();
                return FenValue.Undefined;
            }

            private IValue RemoveItem(IValue[] args, IValue thisVal)
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

            private IValue Clear(IValue[] args, IValue thisVal)
            {
                var store = GetStore();
                store.Clear();
                UpdateLength();
                _onMutate?.Invoke();
                return FenValue.Undefined;
            }

            private IValue Key(IValue[] args, IValue thisVal)
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
