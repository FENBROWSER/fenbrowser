using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FenBrowser.FenEngine.Storage
{
    /// <summary>
    /// Abstraction layer for IndexedDB storage backend.
    /// Allows switching between in-memory (testing) and persistent (production) storage.
    /// All operations are async for proper event loop integration.
    /// </summary>
    public interface IStorageBackend
    {
        /// <summary>
        /// Open or create a database
        /// </summary>
        /// <param name="origin">Origin for partitioning</param>
        /// <param name="name">Database name</param>
        /// <param name="version">Schema version</param>
        /// <returns>True if upgrade needed, false if opened existing</returns>
        Task<DatabaseOpenResult> OpenDatabase(string origin, string name, int version);

        /// <summary>
        /// Delete a database
        /// </summary>
        Task DeleteDatabase(string origin, string name);

        /// <summary>
        /// Get database info
        /// </summary>
        Task<DatabaseInfo> GetDatabaseInfo(string origin, string name);

        /// <summary>
        /// Create an object store in a database
        /// </summary>
        Task CreateObjectStore(string origin, string dbName, string storeName, ObjectStoreOptions options);

        /// <summary>
        /// Delete an object store
        /// </summary>
        Task DeleteObjectStore(string origin, string dbName, string storeName);

        /// <summary>
        /// Get a value from an object store
        /// </summary>
        Task<object> Get(string origin, string dbName, string storeName, object key);

        /// <summary>
        /// Put a value into an object store (insert or update)
        /// </summary>
        Task<object> Put(string origin, string dbName, string storeName, object key, object value);

        /// <summary>
        /// Add a value to an object store (insert only, fails if exists)
        /// </summary>
        Task<object> Add(string origin, string dbName, string storeName, object key, object value);

        /// <summary>
        /// Delete a value from an object store
        /// </summary>
        Task Delete(string origin, string dbName, string storeName, object key);

        /// <summary>
        /// Clear all values in an object store
        /// </summary>
        Task Clear(string origin, string dbName, string storeName);

        /// <summary>
        /// Get all keys in an object store
        /// </summary>
        Task<IEnumerable<object>> GetAllKeys(string origin, string dbName, string storeName);

        /// <summary>
        /// Get all values in an object store
        /// </summary>
        Task<IEnumerable<object>> GetAll(string origin, string dbName, string storeName);

        /// <summary>
        /// Count records in an object store
        /// </summary>
        Task<int> Count(string origin, string dbName, string storeName);

        /// <summary>
        /// Close a database connection
        /// </summary>
        Task CloseDatabase(string origin, string name);
    }

    /// <summary>
    /// Result of opening a database
    /// </summary>
    public class DatabaseOpenResult
    {
        public bool Success { get; set; }
        public bool UpgradeNeeded { get; set; }
        public int OldVersion { get; set; }
        public int NewVersion { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Information about a database
    /// </summary>
    public class DatabaseInfo
    {
        public string Name { get; set; }
        public int Version { get; set; }
        public List<string> ObjectStoreNames { get; set; } = new();
    }

    /// <summary>
    /// Options for creating an object store
    /// </summary>
    public class ObjectStoreOptions
    {
        public string KeyPath { get; set; }
        public bool AutoIncrement { get; set; }
    }
}
