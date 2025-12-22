using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FenBrowser.FenEngine.Storage
{
    /// <summary>
    /// In-memory storage backend for IndexedDB (for testing).
    /// Data is not persisted across restarts.
    /// </summary>
    public class InMemoryStorageBackend : IStorageBackend
    {
        private readonly ConcurrentDictionary<string, MemoryDatabaseState> _databases = new();

        #region Database Operations

        public Task<DatabaseOpenResult> OpenDatabase(string origin, string name, int version)
        {
            var dbKey = GetDatabaseKey(origin, name);
            bool isNew = false;

            var state = _databases.GetOrAdd(dbKey, _ =>
            {
                isNew = true;
                return new MemoryDatabaseState
                {
                    Name = name,
                    Origin = origin,
                    Version = 0 // Start at 0, will upgrade to requested version
                };
            });

            bool upgradeNeeded = isNew || version > state.Version;
            int oldVersion = state.Version;

            if (upgradeNeeded)
            {
                state.Version = version;
            }

            return Task.FromResult(new DatabaseOpenResult
            {
                Success = true,
                UpgradeNeeded = upgradeNeeded,
                OldVersion = oldVersion,
                NewVersion = version
            });
        }

        public Task DeleteDatabase(string origin, string name)
        {
            var dbKey = GetDatabaseKey(origin, name);
            _databases.TryRemove(dbKey, out _);
            return Task.CompletedTask;
        }

        public Task<DatabaseInfo> GetDatabaseInfo(string origin, string name)
        {
            var state = GetState(origin, name);
            if (state == null)
                return Task.FromResult<DatabaseInfo>(null);

            return Task.FromResult(new DatabaseInfo
            {
                Name = state.Name,
                Version = state.Version,
                ObjectStoreNames = state.ObjectStores.Keys.ToList()
            });
        }

        public Task CloseDatabase(string origin, string name)
        {
            // No-op for in-memory
            return Task.CompletedTask;
        }

        #endregion

        #region Object Store Operations

        public Task CreateObjectStore(string origin, string dbName, string storeName, ObjectStoreOptions options)
        {
            var state = GetState(origin, dbName);
            if (state == null)
                throw new InvalidOperationException($"Database '{dbName}' not found");

            if (state.ObjectStores.ContainsKey(storeName))
                throw new InvalidOperationException($"Object store '{storeName}' already exists");

            state.ObjectStores[storeName] = new MemoryObjectStore
            {
                Name = storeName,
                KeyPath = options?.KeyPath,
                AutoIncrement = options?.AutoIncrement ?? false
            };

            return Task.CompletedTask;
        }

        public Task DeleteObjectStore(string origin, string dbName, string storeName)
        {
            var state = GetState(origin, dbName);
            state?.ObjectStores.Remove(storeName);
            return Task.CompletedTask;
        }

        #endregion

        #region CRUD Operations

        public Task<object> Get(string origin, string dbName, string storeName, object key)
        {
            var store = GetStore(origin, dbName, storeName);
            var keyStr = key?.ToString() ?? "null";
            return Task.FromResult(store?.Records.TryGetValue(keyStr, out var value) == true ? value : null);
        }

        public Task<object> Put(string origin, string dbName, string storeName, object key, object value)
        {
            var store = GetStore(origin, dbName, storeName);
            if (store == null)
                throw new InvalidOperationException($"Object store '{storeName}' not found");

            var keyStr = key?.ToString() ?? GenerateKey(store);
            store.Records[keyStr] = value;
            return Task.FromResult<object>(key ?? keyStr);
        }

        public Task<object> Add(string origin, string dbName, string storeName, object key, object value)
        {
            var store = GetStore(origin, dbName, storeName);
            if (store == null)
                throw new InvalidOperationException($"Object store '{storeName}' not found");

            var keyStr = key?.ToString() ?? GenerateKey(store);
            if (store.Records.ContainsKey(keyStr))
                throw new InvalidOperationException($"Key already exists: {keyStr}");

            store.Records[keyStr] = value;
            return Task.FromResult<object>(key ?? keyStr);
        }

        public Task Delete(string origin, string dbName, string storeName, object key)
        {
            var store = GetStore(origin, dbName, storeName);
            store?.Records.Remove(key?.ToString() ?? "null");
            return Task.CompletedTask;
        }

        public Task Clear(string origin, string dbName, string storeName)
        {
            var store = GetStore(origin, dbName, storeName);
            store?.Records.Clear();
            return Task.CompletedTask;
        }

        public Task<IEnumerable<object>> GetAllKeys(string origin, string dbName, string storeName)
        {
            var store = GetStore(origin, dbName, storeName);
            var keys = store?.Records.Keys.Cast<object>().ToList() ?? new List<object>();
            return Task.FromResult<IEnumerable<object>>(keys);
        }

        public Task<IEnumerable<object>> GetAll(string origin, string dbName, string storeName)
        {
            var store = GetStore(origin, dbName, storeName);
            var values = store?.Records.Values.ToList() ?? new List<object>();
            return Task.FromResult<IEnumerable<object>>(values);
        }

        public Task<int> Count(string origin, string dbName, string storeName)
        {
            var store = GetStore(origin, dbName, storeName);
            return Task.FromResult(store?.Records.Count ?? 0);
        }

        #endregion

        #region Helpers

        private string GetDatabaseKey(string origin, string name) => $"{origin}|{name}";

        private MemoryDatabaseState GetState(string origin, string name)
        {
            var dbKey = GetDatabaseKey(origin, name);
            return _databases.TryGetValue(dbKey, out var state) ? state : null;
        }

        private MemoryObjectStore GetStore(string origin, string dbName, string storeName)
        {
            var state = GetState(origin, dbName);
            if (state == null) return null;
            return state.ObjectStores.TryGetValue(storeName, out var store) ? store : null;
        }

        private string GenerateKey(MemoryObjectStore store)
        {
            if (store.AutoIncrement)
            {
                return (store.AutoIncrementValue++).ToString();
            }
            return Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Clear all databases (for testing)
        /// </summary>
        public void Clear()
        {
            _databases.Clear();
        }

        #endregion

        #region Internal State Classes

        private class MemoryDatabaseState
        {
            public string Name { get; set; }
            public string Origin { get; set; }
            public int Version { get; set; }
            public Dictionary<string, MemoryObjectStore> ObjectStores { get; } = new();
        }

        private class MemoryObjectStore
        {
            public string Name { get; set; }
            public string KeyPath { get; set; }
            public bool AutoIncrement { get; set; }
            public long AutoIncrementValue { get; set; } = 1;
            public Dictionary<string, object> Records { get; } = new();
        }

        #endregion
    }
}
