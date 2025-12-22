using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Storage
{
    /// <summary>
    /// File-based persistent storage backend for IndexedDB.
    /// Stores data in JSON files partitioned by origin.
    /// </summary>
    public class FileStorageBackend : IStorageBackend
    {
        private readonly string _basePath;
        private readonly ConcurrentDictionary<string, DatabaseState> _databases = new();
        private readonly JsonSerializerOptions _jsonOptions;

        public FileStorageBackend(string basePath = null)
        {
            _basePath = basePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FenBrowser", "IndexedDB");

            Directory.CreateDirectory(_basePath);

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            FenLogger.Debug($"[FileStorageBackend] Initialized at: {_basePath}", LogCategory.Storage);
        }

        #region Database Operations

        public async Task<DatabaseOpenResult> OpenDatabase(string origin, string name, int version)
        {
            var dbKey = GetDatabaseKey(origin, name);
            var dbPath = GetDatabasePath(origin, name);

            try
            {
                DatabaseState state;
                if (_databases.TryGetValue(dbKey, out state))
                {
                    // Already open
                    if (state.Version == version)
                    {
                        return new DatabaseOpenResult { Success = true, UpgradeNeeded = false };
                    }
                    else if (version > state.Version)
                    {
                        return new DatabaseOpenResult
                        {
                            Success = true,
                            UpgradeNeeded = true,
                            OldVersion = state.Version,
                            NewVersion = version
                        };
                    }
                }

                // Load from disk or create new
                state = await LoadDatabaseState(dbPath);
                int oldVersion = state?.Version ?? 0;

                if (state == null)
                {
                    state = new DatabaseState
                    {
                        Name = name,
                        Origin = origin,
                        Version = version,
                        ObjectStores = new Dictionary<string, ObjectStoreState>()
                    };
                }

                bool upgradeNeeded = version > oldVersion;
                if (upgradeNeeded)
                {
                    state.Version = version;
                }

                _databases[dbKey] = state;
                await SaveDatabaseState(dbPath, state);

                FenLogger.Debug($"[FileStorageBackend] Opened database: {name} v{version} (origin: {origin})", LogCategory.Storage);

                return new DatabaseOpenResult
                {
                    Success = true,
                    UpgradeNeeded = upgradeNeeded,
                    OldVersion = oldVersion,
                    NewVersion = version
                };
            }
            catch (Exception ex)
            {
                FenLogger.Debug($"[FileStorageBackend] OpenDatabase error: {ex.Message}", LogCategory.Errors);
                return new DatabaseOpenResult { Success = false, Error = ex.Message };
            }
        }

        public async Task DeleteDatabase(string origin, string name)
        {
            var dbKey = GetDatabaseKey(origin, name);
            var dbPath = GetDatabasePath(origin, name);

            _databases.TryRemove(dbKey, out _);

            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }

            FenLogger.Debug($"[FileStorageBackend] Deleted database: {name}", LogCategory.Storage);
            await Task.CompletedTask;
        }

        public async Task<DatabaseInfo> GetDatabaseInfo(string origin, string name)
        {
            var state = await GetDatabaseState(origin, name);
            if (state == null)
                return null;

            return new DatabaseInfo
            {
                Name = state.Name,
                Version = state.Version,
                ObjectStoreNames = state.ObjectStores.Keys.ToList()
            };
        }

        public async Task CloseDatabase(string origin, string name)
        {
            var dbKey = GetDatabaseKey(origin, name);
            var dbPath = GetDatabasePath(origin, name);

            if (_databases.TryGetValue(dbKey, out var state))
            {
                await SaveDatabaseState(dbPath, state);
            }

            FenLogger.Debug($"[FileStorageBackend] Closed database: {name}", LogCategory.Storage);
        }

        #endregion

        #region Object Store Operations

        public async Task CreateObjectStore(string origin, string dbName, string storeName, ObjectStoreOptions options)
        {
            var state = await GetDatabaseState(origin, dbName);
            if (state == null)
                throw new InvalidOperationException($"Database '{dbName}' not found");

            if (state.ObjectStores.ContainsKey(storeName))
                throw new InvalidOperationException($"Object store '{storeName}' already exists");

            state.ObjectStores[storeName] = new ObjectStoreState
            {
                Name = storeName,
                KeyPath = options?.KeyPath,
                AutoIncrement = options?.AutoIncrement ?? false,
                Records = new Dictionary<string, object>(),
                AutoIncrementValue = 1
            };

            await SaveDatabaseState(GetDatabasePath(origin, dbName), state);
            FenLogger.Debug($"[FileStorageBackend] Created object store: {storeName}", LogCategory.Storage);
        }

        public async Task DeleteObjectStore(string origin, string dbName, string storeName)
        {
            var state = await GetDatabaseState(origin, dbName);
            if (state == null)
                return;

            state.ObjectStores.Remove(storeName);
            await SaveDatabaseState(GetDatabasePath(origin, dbName), state);
        }

        #endregion

        #region CRUD Operations

        public async Task<object> Get(string origin, string dbName, string storeName, object key)
        {
            var store = await GetObjectStoreState(origin, dbName, storeName);
            if (store == null)
                return null;

            var keyStr = SerializeKey(key);
            return store.Records.TryGetValue(keyStr, out var value) ? value : null;
        }

        public async Task<object> Put(string origin, string dbName, string storeName, object key, object value)
        {
            var state = await GetDatabaseState(origin, dbName);
            var store = GetObjectStore(state, storeName);

            var keyStr = key != null ? SerializeKey(key) : GenerateKey(store);
            store.Records[keyStr] = value;

            await SaveDatabaseState(GetDatabasePath(origin, dbName), state);
            return key ?? keyStr;
        }

        public async Task<object> Add(string origin, string dbName, string storeName, object key, object value)
        {
            var state = await GetDatabaseState(origin, dbName);
            var store = GetObjectStore(state, storeName);

            var keyStr = key != null ? SerializeKey(key) : GenerateKey(store);
            
            if (store.Records.ContainsKey(keyStr))
                throw new InvalidOperationException($"Key already exists: {keyStr}");

            store.Records[keyStr] = value;
            await SaveDatabaseState(GetDatabasePath(origin, dbName), state);
            return key ?? keyStr;
        }

        public async Task Delete(string origin, string dbName, string storeName, object key)
        {
            var state = await GetDatabaseState(origin, dbName);
            var store = GetObjectStore(state, storeName);

            var keyStr = SerializeKey(key);
            store.Records.Remove(keyStr);

            await SaveDatabaseState(GetDatabasePath(origin, dbName), state);
        }

        public async Task Clear(string origin, string dbName, string storeName)
        {
            var state = await GetDatabaseState(origin, dbName);
            var store = GetObjectStore(state, storeName);

            store.Records.Clear();
            await SaveDatabaseState(GetDatabasePath(origin, dbName), state);
        }

        public async Task<IEnumerable<object>> GetAllKeys(string origin, string dbName, string storeName)
        {
            var store = await GetObjectStoreState(origin, dbName, storeName);
            return store?.Records.Keys.Cast<object>().ToList() ?? new List<object>();
        }

        public async Task<IEnumerable<object>> GetAll(string origin, string dbName, string storeName)
        {
            var store = await GetObjectStoreState(origin, dbName, storeName);
            return store?.Records.Values.ToList() ?? new List<object>();
        }

        public async Task<int> Count(string origin, string dbName, string storeName)
        {
            var store = await GetObjectStoreState(origin, dbName, storeName);
            return store?.Records.Count ?? 0;
        }

        #endregion

        #region Helpers

        private string GetDatabaseKey(string origin, string name) => $"{origin}|{name}";

        private string GetOriginPath(string origin)
        {
            // Sanitize origin for filesystem
            var safe = origin.Replace("://", "_").Replace("/", "_").Replace(":", "_");
            return Path.Combine(_basePath, safe);
        }

        private string GetDatabasePath(string origin, string name)
        {
            var originPath = GetOriginPath(origin);
            Directory.CreateDirectory(originPath);
            return Path.Combine(originPath, $"{name}.json");
        }

        private async Task<DatabaseState> GetDatabaseState(string origin, string name)
        {
            var dbKey = GetDatabaseKey(origin, name);
            if (_databases.TryGetValue(dbKey, out var state))
                return state;

            state = await LoadDatabaseState(GetDatabasePath(origin, name));
            if (state != null)
                _databases[dbKey] = state;

            return state;
        }

        private async Task<ObjectStoreState> GetObjectStoreState(string origin, string dbName, string storeName)
        {
            var state = await GetDatabaseState(origin, dbName);
            if (state == null)
                return null;

            return state.ObjectStores.TryGetValue(storeName, out var store) ? store : null;
        }

        private ObjectStoreState GetObjectStore(DatabaseState state, string storeName)
        {
            if (state == null)
                throw new InvalidOperationException("Database not open");

            if (!state.ObjectStores.TryGetValue(storeName, out var store))
                throw new InvalidOperationException($"Object store '{storeName}' not found");

            return store;
        }

        private async Task<DatabaseState> LoadDatabaseState(string path)
        {
            if (!File.Exists(path))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize<DatabaseState>(json, _jsonOptions);
            }
            catch (Exception ex)
            {
                FenLogger.Debug($"[FileStorageBackend] Load error: {ex.Message}", LogCategory.Errors);
                return null;
            }
        }

        private async Task SaveDatabaseState(string path, DatabaseState state)
        {
            try
            {
                var json = JsonSerializer.Serialize(state, _jsonOptions);
                await File.WriteAllTextAsync(path, json);
            }
            catch (Exception ex)
            {
                FenLogger.Debug($"[FileStorageBackend] Save error: {ex.Message}", LogCategory.Errors);
            }
        }

        private string SerializeKey(object key)
        {
            if (key == null) return "null";
            if (key is string s) return s;
            if (key is int or long or double or float) return key.ToString();
            return JsonSerializer.Serialize(key, _jsonOptions);
        }

        private string GenerateKey(ObjectStoreState store)
        {
            if (store.AutoIncrement)
            {
                return (store.AutoIncrementValue++).ToString();
            }
            return Guid.NewGuid().ToString();
        }

        #endregion
    }

    #region Internal State Classes

    internal class DatabaseState
    {
        public string Name { get; set; }
        public string Origin { get; set; }
        public int Version { get; set; }
        public Dictionary<string, ObjectStoreState> ObjectStores { get; set; } = new();
    }

    internal class ObjectStoreState
    {
        public string Name { get; set; }
        public string KeyPath { get; set; }
        public bool AutoIncrement { get; set; }
        public long AutoIncrementValue { get; set; } = 1;
        public Dictionary<string, object> Records { get; set; } = new();
    }

    #endregion
}
